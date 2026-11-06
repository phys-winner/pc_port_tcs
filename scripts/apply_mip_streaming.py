"""Apply only reviewed Texture2D flag objects, preserving concurrent shader/compute edits."""
from pathlib import Path
import argparse
import hashlib
import json
import UnityPy

ROOT = Path(__file__).resolve().parents[1]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def apply(candidate, staging, evidence):
    candidate = candidate.resolve(strict=True)
    if not candidate.is_relative_to(ROOT / "work"):
        raise ValueError("Candidate must stay under work/")
    staging = staging.resolve()
    if not staging.is_relative_to(ROOT / "app") or not staging.is_dir():
        raise ValueError("Canonical staging must be an existing directory under app/")
    if not evidence.resolve().is_relative_to(ROOT / "work"):
        raise ValueError("Evidence must stay under work/")
    manifest = json.loads((candidate / "manifest.json").read_text())
    reports = []
    for file in manifest["files"]:
        relative = Path(file["file"])
        assert len(relative.parts) == 1 and relative.suffix == ".assets"
        path = staging / relative
        env = UnityPy.load(str(path))
        before = {obj.path_id: digest(obj.get_raw_data()) for obj in env.objects}
        objects = {obj.path_id: obj for obj in env.objects}
        changed = {}
        already = 0
        for row in file["textures"]:
            obj = objects[row["pathId"]]
            assert obj.type.name == "Texture2D"
            current = digest(obj.get_raw_data())
            if current == row["newSha256"]:
                already += 1
                continue
            assert current == row["oldSha256"], "Texture preimage differs: " + file["file"] + ":" + str(obj.path_id)
            raw_path = (candidate / row["objectFile"]).resolve(strict=True)
            assert raw_path.is_relative_to(candidate)
            raw = raw_path.read_bytes()
            assert digest(raw) == row["newSha256"]
            original = obj.get_raw_data()
            assert len(raw) == len(original) and [(a, b) for a, b in zip(original, raw) if a != b] == [(0, 1)], "Only one false-to-true byte may change"
            obj.set_raw_data(raw)
            changed[obj.path_id] = row
        temporary = path.with_name(path.name + ".mip-streaming.tmp")
        temporary.write_bytes(env.file.save())
        check = UnityPy.load(str(temporary))
        by_id = {obj.path_id: obj for obj in check.objects}
        assert set(before) == set(by_id)
        for path_id, obj in by_id.items():
            expected = changed[path_id]["newSha256"] if path_id in changed else before[path_id]
            assert digest(obj.get_raw_data()) == expected, "Object changed unexpectedly before atomic replacement"
        env.file.reader.dispose()
        check.file.reader.dispose()
        temporary.replace(path)
        # Re-open actual persisted canonical input, avoiding the previous stale export class of bug.
        persisted = UnityPy.load(str(path))
        for obj in persisted.objects:
            if obj.path_id in changed:
                assert digest(obj.get_raw_data()) == changed[obj.path_id]["newSha256"] and obj.read_typetree()["m_StreamingMipmaps"]
        persisted.file.reader.dispose()
        reports.append({"file": file["file"], "changed": len(changed), "alreadyTarget": already,
            "unchangedObjectsVerified": len(before) - len(changed), "persistedCanonicalVerified": True})
        print(file["file"], "flag edits", len(changed), "already target", already, flush=True)
    evidence.parent.mkdir(parents=True, exist_ok=True)
    evidence.write_text(json.dumps({"candidateManifestSha256": digest((candidate / "manifest.json").read_bytes()), "payloadBytesChanged": 0,
        "qualitySettingsChanged": False, "files": reports}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--staging", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    options = parser.parse_args()
    apply(options.candidate, options.staging, options.evidence)
