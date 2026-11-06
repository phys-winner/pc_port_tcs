"""Apply a reviewed lower-mip texture profile without replacing Shader/Audio objects."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil

import UnityPy
from convert_textures import range_sha, sha, semantic_sha

ROOT = Path(__file__).resolve().parents[1]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def validate_fields(old, new, row):
    allowed = set(row["allowedFields"])
    assert allowed == ({"m_Width", "m_Height", "m_MipCount", "m_CompleteImageSize", "m_StreamData"} if row["mipDrop"] else {"m_StreamData"})
    assert {k: v for k, v in old.items() if k not in allowed} == {k: v for k, v in new.items() if k not in allowed}, "Unauthorized metadata change"
    assert old["m_StreamData"] == row["oldStream"] and new["m_StreamData"] == row["newStream"]
    assert old["m_StreamData"]["path"] == new["m_StreamData"]["path"], "Native split path alias changed"
    if not row["mipDrop"]:
        assert old["m_StreamData"]["size"] == new["m_StreamData"]["size"]


def apply(candidate, staging, evidence, verify_only=False):
    candidate, staging = candidate.resolve(strict=True), staging.resolve(strict=True)
    assert candidate.is_relative_to(ROOT / "work") and staging == (ROOT / "app/obb/assets/bin/Data").resolve()
    assert evidence.resolve().is_relative_to(ROOT / "work")
    manifest = json.loads((candidate / "manifest.json").read_text())
    assert manifest["profile"] in ("mobile2k_existing_mips", "mobile_existing_mips") and manifest["maxDimension"] in (1024, 2048)
    stream_states = {}
    for row in manifest["streams"]:
        assert Path(row["file"]).name == row["file"] and row["file"].endswith(".resS")
        original, target = staging / row["file"], candidate / row["file"]
        assert target.stat().st_size == row["newBytes"] and sha(target) == row["newSha256"], "Candidate stream differs"
        current = sha(original)
        assert current in (row["oldSha256"], row["newSha256"]), "Canonical stream changed after candidate preparation"
        stream_states[row["file"]] = current
        for texture in row["textures"]:
            assert range_sha(target, texture["newOffset"], texture["newSize"]) == texture["newPayloadSha256"]
            if current == row["oldSha256"]:
                assert range_sha(original, texture["oldOffset"], texture["oldSize"]) == texture["oldPayloadSha256"]
                assert range_sha(original, texture["oldOffset"] + texture["skipBytes"], texture["newSize"]) == texture["newPayloadSha256"]
        for gap in row["gaps"]:
            assert range_sha(target, gap["newOffset"], gap["size"]) == gap["sha256"]
            if current == row["oldSha256"]:
                assert range_sha(original, gap["oldOffset"], gap["size"]) == gap["sha256"], "Unrelated payload range differs"
    # Validate every per-object input and changed-field contract before staging
    # anything. Current Shader/Audio/Compute objects may legitimately be newer.
    for file in manifest["files"]:
        assert Path(file["file"]).name == file["file"] and (file["file"].endswith(".assets") or file["file"] in ("level0", "level1"))
        env = UnityPy.load((staging / file["file"]).read_bytes())
        objects = {obj.path_id: obj for obj in env.objects}
        candidate_env = UnityPy.load((candidate / file["file"]).read_bytes())
        candidate_objects = {obj.path_id: obj for obj in candidate_env.objects}
        for row in file["objects"]:
            obj = objects[row["pathId"]]
            assert obj.type.name == row["type"] and row["type"] in {"Texture2D", "Mesh", "Cubemap", "Texture3D", "Texture2DArray", "CubemapArray"}
            current = digest(obj.get_raw_data())
            assert current in (row["oldSha256"], row["newSha256"]), "Current stream consumer differs: " + file["file"] + ":" + str(obj.path_id)
            raw_path = (candidate / row["objectFile"]).resolve(strict=True)
            assert raw_path.is_relative_to(candidate)
            replacement = raw_path.read_bytes()
            assert digest(replacement) == row["newSha256"]
            old = obj.read_typetree()
            target = candidate_objects[obj.path_id]
            assert digest(target.get_raw_data()) == row["newSha256"]
            new = target.read_typetree()
            assert semantic_sha(new) == row["newSemanticSha256"]
            if current == row["oldSha256"]:
                assert semantic_sha(old) == row["oldSemanticSha256"]
                validate_fields(old, new, row)
            else:
                assert semantic_sha(old) == row["newSemanticSha256"]
    if verify_only:
        print("PASS: stream SHA/range, all current object preimages and authorized metadata fields verified; no files changed")
        return
    prepared = []
    reports = []
    for file in manifest["files"]:
        path = staging / file["file"]
        original = path.read_bytes()
        env = UnityPy.load(original)
        before = {obj.path_id: digest(obj.get_raw_data()) for obj in env.objects}
        objects = {obj.path_id: obj for obj in env.objects}
        expected = {}
        changed = 0
        for row in file["objects"]:
            obj = objects[row["pathId"]]
            current = before[obj.path_id]
            assert current in (row["oldSha256"], row["newSha256"])
            expected[obj.path_id] = row["newSha256"]
            if current == row["oldSha256"]:
                obj.set_raw_data((candidate / row["objectFile"]).read_bytes())
                changed += 1
        temporary = path.with_name(path.name + ".mobile-mips.tmp")
        temporary.write_bytes(env.file.save())
        check = UnityPy.load(str(temporary))
        try:
            saved = {obj.path_id: obj for obj in check.objects}
            assert saved.keys() == before.keys()
            for key, obj in saved.items():
                assert digest(obj.get_raw_data()) == expected.get(key, before[key]), "Unrelated Shader/Audio/object changed"
        finally:
            check.file.reader.dispose()
        assert path.read_bytes() == original, "Canonical metadata changed concurrently"
        prepared.append((temporary, path, digest(original)))
        reports.append({"file": file["file"], "changedObjects": changed, "unchangedRawObjectsVerified": len(before) - changed})
    for row in manifest["streams"]:
        path = staging / row["file"]
        if stream_states[row["file"]] == row["newSha256"]:
            continue
        temporary = path.with_name(path.name + ".mobile-mips.tmp")
        shutil.copyfile(candidate / row["file"], temporary)
        assert sha(temporary) == row["newSha256"] and sha(path) == row["oldSha256"]
        prepared.append((temporary, path, row["oldSha256"]))
    # All data is already staged and checked. Parent must perform the replacement
    # while no build/package process or running game reads these loose inputs.
    for temporary, path, expected in prepared:
        assert sha(path) == expected, "Canonical input changed concurrently; refusing replacement"
    for temporary, path, expected in prepared:
        temporary.replace(path)
    for row in manifest["streams"]:
        assert sha(staging / row["file"]) == row["newSha256"]
    for file in manifest["files"]:
        env = UnityPy.load(str(staging / file["file"]))
        try:
            saved = {obj.path_id: obj for obj in env.objects}
            for row in file["objects"]:
                assert digest(saved[row["pathId"]].get_raw_data()) == row["newSha256"]
        finally:
            env.file.reader.dispose()
    evidence.parent.mkdir(parents=True, exist_ok=True)
    evidence.write_text(json.dumps({"candidateManifestSha256": sha(candidate / "manifest.json"), "removedPayloadBytes": manifest["removedPayloadBytes"],
        "selectedTextures": manifest["selectedTextures"], "allUnrelatedObjectsPreserved": True, "persistedCanonicalVerified": True, "files": reports}, indent=2))
    print("PASS: mobile mip stream/object replacements persisted; concurrent Shader/Audio/Compute objects preserved")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--staging", type=Path, default=ROOT / "app/obb/assets/bin/Data")
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    apply(args.candidate, args.staging, args.evidence, args.verify_only)
