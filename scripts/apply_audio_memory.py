"""Apply only guarded Vorbis AudioClip load-type bytes; preserve all other objects."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import tempfile

import UnityPy

ROOT = Path(__file__).resolve().parents[1]


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def require(value, message):
    if not value:
        raise ValueError(message)


def state(file):
    return file.unity_version, int(file.target_platform), [(e.path, e.guid.hex(), e.type) for e in file.externals]


def apply(candidate, target_root, commit):
    candidate, target_root = candidate.resolve(strict=True), target_root.resolve(strict=True)
    require(candidate.is_relative_to(ROOT / "work"), "Audio candidate must stay inside work")
    require(target_root.is_relative_to(ROOT / "app") or target_root.is_relative_to(ROOT / "work"), "Audio staging must stay inside app or work")
    manifest = json.loads((candidate / "manifest.json").read_text())
    results = []
    for file in manifest["files"]:
        relative = Path(file["file"])
        require(len(relative.parts) == 1 and relative.suffix == ".assets", "Invalid audio metadata filename")
        target = target_root / relative
        require(target.resolve(strict=True).is_relative_to(target_root), "Audio metadata target escaped staging")
        env = UnityPy.load(target.read_bytes())
        before_state = state(env.file)
        objects = {obj.path_id: obj for obj in env.objects}
        before = {key: sha(obj.get_raw_data()) for key, obj in objects.items()}
        changed, expected = {}, {}
        for row in file["clips"]:
            obj = objects[row["path_id"]]
            require(obj.type.name == "AudioClip", "Audio object type mismatch")
            require(before[obj.path_id] in {row["old_sha256"], row["new_sha256"]}, "AudioClip preimage differs")
            tree = obj.read_typetree()
            require(tree["m_Name"] == row["name"] and tree["m_CompressionFormat"] == 1, "Audio identity/codec mismatch")
            raw_path = (candidate / row["object_file"]).resolve(strict=True)
            require(raw_path.is_relative_to(candidate), "Generated audio object escaped candidate")
            raw = raw_path.read_bytes()
            require(sha(raw) == row["new_sha256"], "Generated AudioClip hash changed")
            if before[obj.path_id] == row["new_sha256"]:
                require(tree["m_LoadType"] == 1, "Already-target clip has wrong load type")
                continue
            require(tree["m_LoadType"] == 0, "Only DecompressOnLoad Vorbis clips are eligible")
            original = obj.get_raw_data()
            delta = [i for i, (a, b) in enumerate(zip(original, raw)) if a != b]
            require(len(original) == len(raw) and delta == [row["changed_byte_offset"]] and original[delta[0]] == 0 and raw[delta[0]] == 1, "Only one load-type byte0->1 may change")
            tree["m_LoadType"] = 1
            expected[obj.path_id] = tree
            obj.set_raw_data(raw)
            changed[obj.path_id] = row
        serialized = env.file.save()
        check = UnityPy.load(serialized)
        saved = {obj.path_id: obj for obj in check.objects}
        require(saved.keys() == before.keys() and state(check.file) == before_state, "Audio staging changed IDs/platform/external aliases")
        for key, obj in saved.items():
            if key in changed:
                require(sha(obj.get_raw_data()) == changed[key]["new_sha256"] and obj.read_typetree() == expected[key], "Audio semantic/raw roundtrip mismatch")
            else:
                require(sha(obj.get_raw_data()) == before[key], "Unrelated serialized object changed")
        report = {"file": relative.as_posix(), "changed_clips": len(changed), "already_target": len(file["clips"]) - len(changed),
                  "unrelated_raw_objects_verified": len(before) - len(changed), "ids_platform_external_aliases_preserved": True,
                  "payload_bytes_changed": 0, "before_sha256": sha(target.read_bytes()), "after_sha256": sha(serialized), "commit": commit}
        env.file.reader.dispose(); check.file.reader.dispose()
        if commit:
            temporary = target.with_name(target.name + ".audio-staging")
            require(not temporary.exists(), "A prior audio temporary file requires inspection")
            temporary.write_bytes(serialized)
            require(sha(temporary.read_bytes()) == report["after_sha256"], "Audio temporary file write mismatch")
            temporary.replace(target)
        results.append(report)
    return results


def selftest(candidate, source):
    base = ROOT / "work/audio_memory_candidate"
    with tempfile.TemporaryDirectory(prefix="staging-test-", dir=base) as directory:
        root = Path(directory)
        manifest = json.loads((candidate / "manifest.json").read_text())
        for file in manifest["files"]:
            (root / file["file"]).write_bytes((source / file["file"]).read_bytes())
        before = {p.name: sha(p.read_bytes()) for p in root.iterdir()}
        dry = apply(candidate, root, False)
        require(before == {p.name: sha(p.read_bytes()) for p in root.iterdir()}, "Audio dry plan changed fixture")
        first = apply(candidate, root, True)
        second = apply(candidate, root, True)
        require(all(not r["changed_clips"] for r in second), "Audio staging is not idempotent")
        (base / "staging-selftest.json").write_text(json.dumps({"dry": dry, "first": first, "idempotent": second}, indent=2))
    print("AUDIO_STAGING_SELFTEST_PASSED")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, default=ROOT / "work/audio_memory_candidate")
    parser.add_argument("--staging", type=Path, default=ROOT / "app/obb/assets/bin/Data")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        selftest(args.candidate, args.staging)
    else:
        print(json.dumps(apply(args.candidate, args.staging, args.apply), indent=2))
