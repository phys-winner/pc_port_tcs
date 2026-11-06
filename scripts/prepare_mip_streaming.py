"""Prepare one-byte Texture2D streaming flag edits; keep every mip/payload unchanged."""
from pathlib import Path
import argparse
import hashlib
import json
import UnityPy

ROOT = Path(__file__).resolve().parents[1]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def prepare(input_root, output, audit):
    output = output.resolve()
    if not output.is_relative_to(ROOT / "work"):
        raise ValueError("Candidate output must stay under work/")
    if (output / "manifest.json").exists():
        raise FileExistsError("Preserve completed candidate; choose a fresh output")
    evidence = json.loads(audit.read_text())
    selected = {}
    for row in evidence["textures"]:
        if row["stockUv0MaterialCandidate"]:
            selected.setdefault(row["file"], {})[row["path_id"]] = row
    output.mkdir(parents=True, exist_ok=True)
    (output / "objects").mkdir(exist_ok=True)
    manifest = {"audit": str(audit.resolve()), "auditSha256": file_digest(audit), "inputRoot": str(input_root.resolve()),
        "filter": "All material uses are approved stock UV0 family and primary UV0 texture slots; Texture2D, external payload, non-readable, multiple mips. Other shader families, no-material-use, UI/LUT/reflection/particles and readable textures are excluded.",
        "qualitySettingsChanged": False, "payloadBytesChanged": 0,
        "limits": ["Material eligibility does not prove absence of a separate dynamic non-material use",
            "Native OBB streaming and requested/calculated/loaded mip levels must be observed on device",
            "Global QualitySettings must be enabled before texture Awake/load by the parent task"], "files": []}
    for filename, rows in selected.items():
        path = input_root / filename
        preimage = file_digest(path)
        env = UnityPy.load(str(path))
        before = {obj.path_id: digest(obj.get_raw_data()) for obj in env.objects}
        changes = []
        for obj in env.objects:
            if obj.path_id not in rows:
                continue
            if obj.type.name != "Texture2D":
                raise ValueError("Selected source ID is not Texture2D")
            source = rows[obj.path_id]
            tree = obj.read_typetree()
            assert not tree["m_IsReadable"] and tree["m_MipCount"] > 1 and tree["m_ImageCount"] == 1
            assert tree["m_StreamData"]["path"] and tree["m_StreamData"]["size"] > 0
            assert tree["m_Name"] == source["m_Name"] and tree["m_Width"] == source["m_Width"] and tree["m_Height"] == source["m_Height"]
            raw_before = obj.get_raw_data()
            if tree["m_StreamingMipmaps"]:
                continue
            tree["m_StreamingMipmaps"] = True
            # UnityPy.get_raw_data() reads the old reader until the container is
            # persisted; save_typetree's return value is the pending new bytes.
            raw_after = obj.save_typetree(tree)
            assert len(raw_before) == len(raw_after), "Flag edit changed object size"
            different = [n for n, (a, b) in enumerate(zip(raw_before, raw_after)) if a != b]
            assert len(different) == 1 and raw_before[different[0]] == 0 and raw_after[different[0]] == 1, "Flag edit was not exactly one false-to-true byte"
            changes.append({"pathId": obj.path_id, "name": tree["m_Name"], "oldSha256": digest(raw_before), "newSha256": digest(raw_after),
                "flagByteOffset": different[0], "payload": dict(tree["m_StreamData"]), "width": tree["m_Width"], "height": tree["m_Height"],
                "mipCount": tree["m_MipCount"], "format": tree["m_TextureFormat"], "materialUseCount": len(source["materialUses"]),
                "allMaterialShaders": source["allMaterialShaders"]})
        assert file_digest(path) == preimage, "Input changed concurrently; retry from stable metadata"
        candidate = output / filename
        candidate.write_bytes(env.file.save())
        check = UnityPy.load(str(candidate))
        by_id = {obj.path_id: obj for obj in check.objects}
        assert set(before) == set(by_id)
        changed = {row["pathId"]: row for row in changes}
        for path_id, obj in by_id.items():
            raw = obj.get_raw_data()
            if path_id in changed:
                row = changed[path_id]
                assert digest(raw) == row["newSha256"]
                assert obj.read_typetree()["m_StreamingMipmaps"]
                export = output / "objects" / (filename + "." + str(path_id) + ".texture")
                export.write_bytes(raw)
                row["objectFile"] = str(export.relative_to(output))
            else:
                assert digest(raw) == before[path_id], "Unselected object changed"
        manifest["files"].append({"file": filename, "sourceSha256": preimage, "candidateSha256": file_digest(candidate),
            "selected": len(rows), "changed": len(changes), "alreadyStreaming": len(rows) - len(changes),
            "unchangedObjectsVerified": len(before) - len(changes), "textures": changes})
        print(filename, "selected", len(rows), "one-byte changes", len(changes), flush=True)
    manifest["selectedCount"] = sum(row["selected"] for row in manifest["files"])
    manifest["changedCount"] = sum(row["changed"] for row in manifest["files"])
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print("PASS: streaming flags only; payload/dimensions/mips/formats/readability and all unselected objects preserved")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--audit", type=Path, default=ROOT / "work/evidence/texture_memory/inventory.json")
    options = parser.parse_args()
    prepare(options.input, options.output, options.audit)
