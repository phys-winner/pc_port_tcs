"""Add work-only ResourceManager aliases to existing Cyrillic-capable game fonts."""
from pathlib import Path
import copy
import hashlib
import json
import UnityPy

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "work/font_candidate"
SOURCE = ROOT / "app/assets/bin/Data/globalgamemanagers"
FONTS = {"closing_shift/fonts/oswald_regular": ("sharedassets0.assets", 1173),
         "closing_shift/fonts/liberation_sans": ("resources.assets", 1968)}


def digest(data): return hashlib.sha256(data).hexdigest()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    env = UnityPy.load(SOURCE.read_bytes())
    before = {obj.path_id: digest(obj.get_raw_data()) for obj in env.objects}
    obj = next(obj for obj in env.objects if obj.type.name == "ResourceManager")
    tree = obj.read_typetree()
    original = copy.deepcopy(tree)
    externals = [item.path.replace("\\", "/").rsplit("/", 1)[-1] for item in env.file.externals]
    aliases = []
    for name, (filename, key) in FONTS.items():
        file_id = externals.index(filename) + 1
        pointer = {"m_FileID": file_id, "m_PathID": key}
        assert not any(entry[0] == name for entry in tree["m_Container"]), "Alias already exists"
        source = ROOT / "app/obb/assets/bin/Data" / filename
        font_env = UnityPy.load(str(source))
        try:
            font = font_env.file.objects[key]
            assert font.type.name == "Font"
            font_tree = font.read_typetree()
            assert font_tree["m_Name"] == ("Oswald-Regular" if "oswald" in name else "LiberationSans") and len(font_tree["m_FontData"]) > 50000
            aliases.append({"name": name, "pointer": pointer, "fontName": font_tree["m_Name"], "fontFile": filename,
                            "fontRawSha256": digest(font.get_raw_data()), "fontDataSha256": digest(bytes(font_tree["m_FontData"]))})
        finally:
            font_env.file.reader.dispose()
        tree["m_Container"].append((name, pointer))
    # Preserve the original lexicographic order and duplicate-key order used by
    # ResourceManager's serialized lookup, inserting the two aliases stably.
    assert original["m_Container"] == sorted(original["m_Container"], key=lambda item: item[0])
    tree["m_Container"].sort(key=lambda item: item[0])
    assert [item for item in tree["m_Container"] if item[0] not in FONTS] == original["m_Container"]
    assert {k: v for k, v in tree.items() if k != "m_Container"} == {k: v for k, v in original.items() if k != "m_Container"}
    raw = obj.save_typetree(tree)
    candidate = OUT / "globalgamemanagers"
    candidate.write_bytes(env.file.save())
    check = UnityPy.load(candidate.read_bytes())
    objects = {item.path_id: item for item in check.objects}
    for key, item in objects.items():
        assert digest(item.get_raw_data()) == (digest(raw) if key == obj.path_id else before[key])
    assert objects[obj.path_id].read_typetree() == tree
    export = OUT / "ResourceManager.font_aliases.bin"
    export.write_bytes(objects[obj.path_id].get_raw_data())
    manifest = {"file": "assets/bin/Data/globalgamemanagers", "pathId": obj.path_id, "type": "ResourceManager",
                "oldRawSha256": before[obj.path_id], "newRawSha256": digest(export.read_bytes()), "objectFile": export.name,
                "aliases": aliases, "onlyAdditiveResourcesEntries": True, "unchangedRawObjectsVerified": len(before) - 1,
                "originalFontPayloadsDuplicated": False, "newFontDependenciesFetched": False}
    (OUT / "font_resource_manifest.json").write_text(json.dumps(manifest, indent=2))
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__": main()
