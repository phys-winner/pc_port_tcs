"""Stage verified compute objects while preserving every unrelated serialized blob."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tempfile

import UnityPy
from UnityPy.enums import BuildTarget

ROOT = Path(__file__).resolve().parents[1]
AURA_IDS = {1823, 1824, 1825, 1826, 1827, 1828, 1830}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def checked(condition, message):
    if not condition:
        raise ValueError(message)


def state(file):
    return {"version": file.unity_version, "platform": int(file.target_platform),
            "externals": [(e.path, e.guid.hex(), e.type) for e in file.externals]}


def verify_tree(tree, name):
    checked(tree["m_Name"] == name, "Compute object identity changed")
    variants = [v for v in tree["variants"] if v["targetRenderer"] == 11]
    checked(len(variants) == 1, "Expected exactly one GLES compute variant")
    variant = variants[0]
    checked(variant["targetLevel"] == 3, "GLES compute targetLevel differs from matching Unity builtin reference")
    for kernel in variant["kernels"]:
        for _, case in kernel["variantMap"]:
            code = bytes(case["code"])
            checked(code.startswith(b"#version 310 es\n") and code.endswith(b"\0"), "GLES compute source framing differs from matching Unity builtin schema")
            checked(len(case["cbs"]) == len(case["cbVariantIndices"]), "Compute cbuffer variant index count mismatch")
            checked(all(0 <= i < len(variant["constantBuffers"]) for i in case["cbVariantIndices"]), "Compute cbuffer index exceeds table")
            checked(variant["resourcesResolved"] is True, "Native semantic resource aliases require resourcesResolved=true")
            checked(all(t["generatedName"] for t in case["textures"]), "Native GLES texture lookup requires a generatedName for direct and combined samplers")


def apply(manifest_path, data_root, commit, all_ready=False, ledger_path=None):
    manifest_path = manifest_path.resolve(strict=True)
    checked(manifest_path.is_relative_to(ROOT / "work"), "Manifest must be a generated workspace artifact")
    data_root = data_root.resolve(strict=True)
    checked(data_root.is_relative_to(ROOT / "app") or data_root.is_relative_to(ROOT / "work"), "Target must stay inside app or work")
    manifest = json.loads(manifest_path.read_text())
    entries = [e for e in manifest["results"] if e.get("object_file") and (all_ready or e["file"] == "resources.assets" and e["path_id"] in AURA_IDS)]
    checked(entries and (all_ready or {e["path_id"] for e in entries} == AURA_IDS), "The complete seven-shader Aura chain is required")
    ledger_path = ledger_path or ROOT / "work/evidence/applied-compute/object-hashes.json"
    ledger = json.loads(ledger_path.read_text()) if ledger_path.exists() else {}
    result = []
    for filename in sorted({e["file"] for e in entries}):
        relative = Path(filename)
        checked(not relative.is_absolute() and len(relative.parts) == 1 and ".." not in relative.parts, "Invalid compute container filename")
        target = data_root / relative
        checked(target.resolve(strict=True).is_relative_to(data_root), "Target escaped data root")
        env = UnityPy.load(target.read_bytes())
        objects = {obj.path_id: obj for obj in env.objects}
        before = {key: sha(obj.get_raw_data()) for key, obj in objects.items()}
        before_state = state(env.file)
        selected = [e for e in entries if e["file"] == filename]
        modified = set()
        for entry in selected:
            key = entry["path_id"]
            checked(key in objects and objects[key].type.name == "ComputeShader", "Compute object path/type mismatch")
            checked(objects[key].read_typetree()["m_Name"] == entry["name"], "Compute shader name mismatch")
            identity = filename + ":" + str(key)
            allowed = {entry["source_object_sha256"], entry["object_sha256"], ledger.get(identity)}
            checked(before[key] in allowed, "Unexpected compute preimage: " + identity)
            raw_path = Path(entry["object_file"]).resolve(strict=True)
            checked(raw_path.is_relative_to(ROOT / "work"), "Generated compute object escaped work directory")
            raw = raw_path.read_bytes()
            checked(sha(raw) == entry["object_sha256"], "Generated compute object hash mismatch")
            if before[key] != entry["object_sha256"]:
                objects[key].set_raw_data(raw)
                modified.add(key)
        serialized = env.file.save()
        check = UnityPy.load(serialized)
        saved_objects = {obj.path_id: obj for obj in check.objects}
        checked(objects.keys() == saved_objects.keys(), "Compute staging changed path IDs")
        checked(before_state == state(check.file), "Compute staging changed version, platform or external path aliases")
        for key in before.keys() - modified:
            checked(sha(saved_objects[key].get_raw_data()) == before[key], "Unrelated object changed: " + str(key))
        for entry in selected:
            obj = saved_objects[entry["path_id"]]
            checked(sha(obj.get_raw_data()) == entry["object_sha256"], "Written compute bytes differ")
            verify_tree(obj.read_typetree(), entry["name"])
        output = {"file": filename, "target": str(target), "changed_path_ids": sorted(modified),
                  "unrelated_raw_objects_verified": len(before) - len(modified), "external_aliases_platform_preserved": True,
                  "before_sha256": sha(target.read_bytes()), "after_sha256": sha(serialized), "commit": commit}
        env.file.reader.dispose()
        check.file.reader.dispose()
        if commit:
            temporary = target.with_name(target.name + ".compute-staging")
            checked(not temporary.exists(), "A prior compute staging file needs inspection")
            temporary.write_bytes(serialized)
            checked(sha(temporary.read_bytes()) == output["after_sha256"], "Compute temporary file write mismatch")
            temporary.replace(target)
            for entry in selected:
                ledger[filename + ":" + str(entry["path_id"])] = entry["object_sha256"]
        result.append(output)
    if commit:
        ledger_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = ledger_path.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(ledger, indent=2))
        temporary.replace(ledger_path)
    return result


def selftest(manifest_path):
    base = ROOT / "work/compute_hlslcc_build"
    base.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="staging-test-", dir=base) as temporary:
        data = Path(temporary)
        source = Path(r"external/game/TheClosingShift_Data\resources.assets")
        target = data / source.name
        shutil.copyfile(source, target)
        env = UnityPy.load(target.read_bytes())
        env.file.target_platform = BuildTarget.Android
        texture = next(obj for obj in env.objects if obj.type.name == "Texture2D")
        tree = texture.read_typetree()
        tree["m_Name"] += "__compute_staging_fixture"
        texture.save_typetree(tree)
        # Model canonical archive path alias changes made by the Android port.
        if env.file.externals:
            env.file.externals[0].path = "archive:/fixture-preserved/" + Path(env.file.externals[0].path).name
        raw = env.file.save()
        env.file.reader.dispose()
        target.write_bytes(raw)
        local_ledger = data / "test-ledger.json"
        dry = apply(manifest_path, data, False, ledger_path=local_ledger)
        checked(sha(target.read_bytes()) == sha(raw), "Dry plan modified its input")
        first = apply(manifest_path, data, True, ledger_path=local_ledger)
        checked(len(first[0]["changed_path_ids"]) == 7, "Aura staging coverage differs")
        second = apply(manifest_path, data, True, ledger_path=local_ledger)
        checked(not second[0]["changed_path_ids"], "Compute staging is not idempotent")
        proof = {"dry_plan": dry, "first_apply": first, "idempotent_apply": second}
        (base / "staging-selftest.json").write_text(json.dumps(proof, indent=2))
        print(json.dumps(proof, indent=2))
    print("COMPUTE_STAGING_SELFTEST_PASSED")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path, nargs="?", default=ROOT / "work/android_compute/manifest.json")
    parser.add_argument("--data-root", type=Path, default=ROOT / "app/obb/assets/bin/Data")
    parser.add_argument("--apply", action="store_true", help="Commit the already verified changes; omitted means dry plan")
    parser.add_argument("--all-ready", action="store_true", help="Also apply non-Aura compute objects with complete validated variants")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        selftest(args.manifest)
    else:
        print(json.dumps(apply(args.manifest, args.data_root, args.apply, args.all_ready), indent=2))
