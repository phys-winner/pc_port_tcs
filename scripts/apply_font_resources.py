"""Merge only reviewed additive existing-font resource aliases into canonical settings."""
from pathlib import Path
import argparse
import hashlib
import json
import UnityPy

ROOT = Path(__file__).resolve().parents[1]


def digest(raw): return hashlib.sha256(raw).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, default=ROOT / "work/font_candidate")
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    candidate = args.candidate.resolve(strict=True)
    assert candidate.is_relative_to(ROOT / "work") and args.evidence.resolve().is_relative_to(ROOT / "work")
    manifest = json.loads((candidate / "font_resource_manifest.json").read_text())
    assert manifest["file"] == "assets/bin/Data/globalgamemanagers" and manifest["type"] == "ResourceManager"
    path = ROOT / "app" / manifest["file"]
    original = path.read_bytes()
    env = UnityPy.load(original)
    objects = {obj.path_id: obj for obj in env.objects}
    before = {key: digest(obj.get_raw_data()) for key, obj in objects.items()}
    obj = objects[manifest["pathId"]]
    assert obj.type.name == "ResourceManager" and before[obj.path_id] in (manifest["oldRawSha256"], manifest["newRawSha256"])
    for alias in manifest["aliases"]:
        source = ROOT / "app/obb/assets/bin/Data" / alias["fontFile"]
        font_env = UnityPy.load(str(source))
        try:
            font = font_env.file.objects[alias["pointer"]["m_PathID"]]
            assert font.type.name == "Font" and digest(font.get_raw_data()) == alias["fontRawSha256"]
            assert digest(bytes(font.read_typetree()["m_FontData"])) == alias["fontDataSha256"]
        finally:
            font_env.file.reader.dispose()
    raw = (candidate / manifest["objectFile"]).read_bytes()
    assert digest(raw) == manifest["newRawSha256"]
    candidate_env = UnityPy.load((candidate / "globalgamemanagers").read_bytes())
    target = candidate_env.file.objects[obj.path_id]
    assert digest(target.get_raw_data()) == manifest["newRawSha256"]
    old_tree, new_tree = obj.read_typetree(), target.read_typetree()
    alias_names = {alias["name"] for alias in manifest["aliases"]}
    assert {k: v for k, v in new_tree.items() if k != "m_Container"} == {k: v for k, v in old_tree.items() if k != "m_Container"}
    if before[obj.path_id] == manifest["oldRawSha256"]:
        assert [entry for entry in new_tree["m_Container"] if entry[0] not in alias_names] == old_tree["m_Container"]
    for alias in manifest["aliases"]:
        assert [ptr for name, ptr in new_tree["m_Container"] if name == alias["name"]] == [alias["pointer"]]
    if args.verify_only:
        print("PASS: original Font payloads and additive ResourceManager alias preimages verified; no files changed")
        return
    obj.set_raw_data(raw)
    output = env.file.save()
    check = UnityPy.load(output)
    for saved in check.objects:
        assert digest(saved.get_raw_data()) == (manifest["newRawSha256"] if saved.path_id == obj.path_id else before[saved.path_id]), "Unrelated settings changed"
    assert path.read_bytes() == original, "Canonical settings changed concurrently"
    temporary = path.with_name(path.name + ".font-resources.tmp")
    temporary.write_bytes(output)
    temporary.replace(path)
    persisted = UnityPy.load(path.read_bytes())
    assert digest(persisted.file.objects[obj.path_id].get_raw_data()) == manifest["newRawSha256"]
    args.evidence.parent.mkdir(parents=True, exist_ok=True)
    args.evidence.write_text(json.dumps({"fontAliasesManifestSha256": digest((candidate / "font_resource_manifest.json").read_bytes()),
        "changedObject": obj.path_id, "unrelatedRawObjectsVerified": len(before) - 1, "originalFontsReused": True, "persistedCanonicalVerified": True}, indent=2))
    print("PASS: two existing-font Resources aliases applied; other settings and all font payloads preserved")


if __name__ == "__main__": main()
