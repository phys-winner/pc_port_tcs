"""Make a work-only mobile texture cap using existing encoded lower mip levels."""
from collections import defaultdict
from pathlib import Path
import argparse
import copy
import hashlib
import json
import math
import re
import struct

import UnityPy
from UnityPy.enums import TextureFormat
from convert_textures import copy_range, range_sha, sha, semantic_sha

ROOT = Path(__file__).resolve().parents[1]
ORIGINAL = Path(r"external/game/TheClosingShift_Data")
STREAM_TYPES = {"Mesh", "Texture2D", "Cubemap", "Texture3D", "Texture2DArray", "CubemapArray"}
BYTE_FORMATS = {1: 1, 2: 2, 3: 3, 4: 4, 5: 4, 7: 2, 9: 2, 14: 4, 15: 2, 16: 4,
                17: 8, 18: 4, 19: 8, 20: 16, 22: 4, 62: 2, 63: 1, 72: 2, 73: 4}
BLOCK_FORMATS = {10: (4, 8), 12: (4, 16), 24: (4, 8), 25: (4, 16), 26: (4, 16), 27: (4, 16),
                 34: (4, 8), 41: (4, 8), 42: (4, 8), 43: (4, 16), 44: (4, 16),
                 45: (4, 8), 46: (4, 8), 47: (4, 16)}
for start in (48, 54, 66):
    BLOCK_FORMATS.update({start + i: (block, 16) for i, block in enumerate((4, 5, 6, 8, 10, 12))})


def digest(data):
    return hashlib.sha256(data).hexdigest()


def normalized(name):
    name = str(name).replace("\\", "/")
    if name.startswith("Library/"):
        name = "Resources/" + name.split("/", 1)[1]
    return name


def mip_sizes(tree):
    fmt, width, height, count = (tree[k] for k in ("m_TextureFormat", "m_Width", "m_Height", "m_MipCount"))
    if fmt in BYTE_FORMATS:
        return [max(1, width >> i) * max(1, height >> i) * BYTE_FORMATS[fmt] for i in range(count)]
    if fmt in BLOCK_FORMATS:
        block, size = BLOCK_FORMATS[fmt]
        return [math.ceil(max(1, width >> i) / block) * math.ceil(max(1, height >> i) / block) * size for i in range(count)]
    raise ValueError("Unreviewed mip byte layout: " + str(fmt))


def pointers(value):
    if isinstance(value, dict):
        if set(value) == {"m_FileID", "m_PathID"}:
            yield value
        else:
            for item in value.values():
                yield from pointers(item)
    elif isinstance(value, list):
        for item in value:
            yield from pointers(item)


def protected_textures(current=False):
    inventory_path = ROOT / "work/evidence/asset_inventory/inventory.json"
    external_path = ROOT / "work/evidence/asset_inventory/externals.json"
    inventory = json.loads(inventory_path.read_text())
    externals = {normalized(k): list(map(normalized, v)) for k, v in json.loads(external_path.read_text()).items()}
    textures = {(normalized(t["file"]), t["path_id"]) for t in inventory["textures"]}
    materials = {(normalized(m["file"]), m["path_id"]): m for m in inventory["materials"]}
    shaders = {(normalized(s["file"]), s["path_id"]): s["name"] for s in inventory["shaders"]}
    protected = defaultdict(list)
    source_hashes, counts = {}, defaultdict(int)

    def resolve(owner, ptr):
        file_id, path_id = ptr["m_FileID"], ptr["m_PathID"]
        if not path_id or file_id < 0 or file_id > len(externals.get(owner, [])):
            return None
        return (owner if file_id == 0 else externals[owner][file_id - 1], path_id)

    def protect(key, reason):
        if key in textures and reason not in protected[key]:
            protected[key].append(reason)

    for key, material in materials.items():
        shader = shaders.get(resolve(key[0], material["m_Shader"]), "")
        if re.search(r"(^UI/|^GUI/|^Sprites/|TextMeshPro|TextMesh|Font|Text Shader)", shader, re.I):
            for name, value in material["m_SavedProperties"].get("m_TexEnvs", []):
                protect(resolve(key[0], value["m_Texture"]), {"kind": "UI/font material", "file": key[0], "pathId": key[1], "shader": shader, "slot": name})
    # Read all immutable metadata, including inactive/future UI; raw pointer scan
    # deliberately overprotects any direct MonoBehaviour texture reference.
    for record in inventory["files"]:
        filename = normalized(record["name"])
        source = ORIGINAL / filename
        if current:
            source = next(p for p in (ROOT / 'app/obb/assets/bin/Data' / filename, ROOT / 'app/assets/bin/Data' / filename) if p.is_file())
        else:
            assert sha(source) == record["sha256"], "Original metadata preimage differs: " + filename
        source_hashes[filename] = sha(source)
        env = UnityPy.load(str(source))
        try:
            if current:
                externals[filename] = [normalized(entry.path) for entry in env.file.externals]
                for obj in env.objects:
                    if obj.type.name != 'Material': continue
                    tree = obj.read_typetree()
                    original = materials.get((filename, obj.path_id))
                    if original is None or tree['m_Shader'] != original['m_Shader'] or dict(tree['m_SavedProperties'].get('m_TexEnvs', [])) != dict(original['m_SavedProperties'].get('m_TexEnvs', [])):
                        for slot, value in tree['m_SavedProperties'].get('m_TexEnvs', []):
                            protect(resolve(filename, value['m_Texture']), {'kind':'current material differs from inventory','file':filename,'pathId':obj.path_id,'slot':slot})
            local_targets = {0: {key[1] for key in textures if key[0] == filename}}
            for index, name in enumerate(externals.get(filename, []), 1):
                local_targets[index] = {key[1] for key in textures if key[0] == name}
            for obj in env.objects:
                kind = obj.type.name
                if kind in ("Sprite", "Font", "SpriteAtlas"):
                    counts[kind] += 1
                    for ptr in pointers(obj.read_typetree()):
                        protect(resolve(filename, ptr), {"kind": kind, "file": filename, "pathId": obj.path_id})
                elif kind == "MonoBehaviour":
                    counts[kind] += 1
                    raw = obj.get_raw_data()
                    for offset in range(0, len(raw) - 11, 4):
                        file_id, path_id = struct.unpack_from("<Iq", raw, offset)
                        if path_id in local_targets.get(file_id, ()):
                            protect(resolve(filename, {"m_FileID": file_id, "m_PathID": path_id}),
                                    {"kind": "conservative direct MonoBehaviour texture PPtr", "file": filename, "pathId": obj.path_id, "rawOffset": offset})
        finally:
            env.file.reader.dispose()
    # The earlier184 audit covered the three game asset containers. This scan
    # additionally includes the builtin resources' Sprite.
    assert counts["Sprite"] >= 184, "Sprite inventory incomplete"
    return protected, {"immutableMetadataHashes": source_hashes, "counts": dict(counts),
                       "protected": [{"file": k[0], "pathId": k[1], "reasons": v} for k, v in sorted(protected.items())]}


def map_range(offset, size, replacements):
    shift = 0
    for row in replacements:
        old, count = row["oldOffset"], row["oldSize"]
        if (offset, size) == (old, count):
            return row["newOffset"], row["newSize"]
        assert offset + size <= old or offset >= old + count, "Unselected stream reference overlaps a mip drop"
        if offset >= old + count:
            shift += row["newSize"] - count
    return offset + shift, size


def prepare(staging, output, cap, selection=None):
    staging, output = staging.resolve(strict=True), output.resolve()
    assert staging == (ROOT / "app/obb/assets/bin/Data").resolve() and output.is_relative_to(ROOT / "work")
    assert cap > 0 and not (output / "manifest.json").exists()
    output.mkdir(parents=True, exist_ok=True)
    (output / "objects").mkdir(exist_ok=True)
    requested = None if selection is None else {(row['file'], row['pathId']): row for row in json.loads(selection.read_text(encoding='utf-8'))}
    protected, protection = protected_textures(current=selection is not None)
    (output / "protected_texture_references.json").write_text(json.dumps(protection, indent=2))
    metadata = sorted(p for p in staging.iterdir() if p.suffix == ".assets" or p.name in ("level0", "level1"))
    streams = {p.name: {"bytes": p.stat().st_size, "sha256": sha(p)} for p in staging.glob("*.resS")}
    consumers, selected, inventory, excluded = defaultdict(list), {}, [], []
    for path in metadata:
        env = UnityPy.load(str(path))
        try:
            for obj in env.objects:
                if obj.type.name not in STREAM_TYPES:
                    assert b".resS" not in obj.get_raw_data(), "Unknown external stream consumer: " + obj.type.name
                    continue
                tree = obj.read_typetree()
                stream = tree.get("m_StreamData", {})
                if not stream.get("size"):
                    continue
                physical = stream["path"].removesuffix(".resource")
                if physical not in streams:
                    continue
                assert Path(physical).name == physical and set(stream) == {"path", "offset", "size"}
                assert 0 <= stream["offset"] <= streams[physical]["bytes"] - stream["size"]
                key = path.name, obj.path_id
                consumers[physical].append({"file": path.name, "pathId": obj.path_id, "type": obj.type.name, "stream": dict(stream)})
                if obj.type.name == "Texture2D":
                    inventory.append({"file": key[0], "pathId": key[1], "size": stream["size"]})
                    if requested is not None and key not in requested: continue
                    texture_cap = cap if requested is None else requested[key].get('maxDimension', cap)
                    if requested is not None:
                        assert tree['m_Name'] == requested[key]['name'] and texture_cap in (512, 1024), 'Selected texture identity/cap differs'
                    if max(tree["m_Width"], tree["m_Height"]) <= texture_cap or tree["m_IsReadable"] or tree["m_MipCount"] <= 1:
                        continue
                    if key in protected:
                        excluded.append({"file": key[0], "pathId": key[1], "name": tree["m_Name"], "reason": "Sprite/UI/font/direct-script-reference protection"})
                        continue
                    assert tree["m_ImageCount"] == 1 and tree["m_TextureDimension"] == 2
                    sizes = mip_sizes(tree)
                    assert sum(sizes) == stream["size"] == tree["m_CompleteImageSize"], "Unexpected mip chain layout"
                    drop = 0
                    while max(max(1, tree["m_Width"] >> drop), max(1, tree["m_Height"] >> drop)) > texture_cap:
                        drop += 1
                    assert 0 < drop < len(sizes)
                    selected[key] = {"file": key[0], "pathId": key[1], "name": tree["m_Name"], "format": tree["m_TextureFormat"],
                        "formatName": TextureFormat(tree["m_TextureFormat"]).name, "width": tree["m_Width"], "height": tree["m_Height"],
                        "mipCount": tree["m_MipCount"], "droppedMips": drop, "mipBytes": sizes, "stream": dict(stream), "physicalStream": physical,
                        "oldOffset": stream["offset"], "oldSize": stream["size"], "skipBytes": sum(sizes[:drop]), "newSize": sum(sizes[drop:])}
        finally:
            env.file.reader.dispose()
    # Any alias or partial overlap excludes the range, so protected consumers
    # cannot silently share a changed texture payload.
    for key, row in list(selected.items()):
        overlaps = [c for c in consumers[row["physicalStream"]] if c["stream"]["offset"] < row["oldOffset"] + row["oldSize"] and c["stream"]["offset"] + c["stream"]["size"] > row["oldOffset"]]
        if len(overlaps) != 1:
            excluded.append({"file": key[0], "pathId": key[1], "name": row["name"], "reason": "Shared/overlapping payload reference"})
            del selected[key]
    assert selected, "No eligible mip drops"
    if requested is not None:
        assert set(selected) == set(requested), 'Every explicit target must pass current protection and stream checks'
    remaps, stream_reports = {}, []
    for name, info in streams.items():
        changes = sorted((r for r in selected.values() if r["physicalStream"] == name), key=lambda r: r["oldOffset"])
        if not changes:
            continue
        old_path, new_path = staging / name, output / name
        gaps, cursor = [], 0
        with old_path.open("rb") as source, new_path.open("wb") as target:
            for row in changes:
                assert row["oldOffset"] >= cursor
                count, new_offset = row["oldOffset"] - cursor, target.tell()
                gap_hash = copy_range(source, target, count)
                gaps.append({"oldOffset": cursor, "newOffset": new_offset, "size": count, "sha256": gap_hash})
                row["oldPayloadSha256"] = range_sha(old_path, row["oldOffset"], row["oldSize"])
                source.seek(row["skipBytes"], 1)
                row["newOffset"] = target.tell()
                row["newPayloadSha256"] = copy_range(source, target, row["newSize"])
                cursor = row["oldOffset"] + row["oldSize"]
            count, new_offset = info["bytes"] - cursor, target.tell()
            gaps.append({"oldOffset": cursor, "newOffset": new_offset, "size": count, "sha256": copy_range(source, target, count)})
        for row in changes:
            assert row["newPayloadSha256"] == range_sha(old_path, row["oldOffset"] + row["skipBytes"], row["newSize"]) == range_sha(new_path, row["newOffset"], row["newSize"])
        for row in gaps:
            assert row["sha256"] == range_sha(new_path, row["newOffset"], row["size"])
        assert sha(old_path) == info["sha256"], "Input payload changed concurrently"
        remaps[name] = changes
        stream_reports.append({"file": name, "oldBytes": info["bytes"], "oldSha256": info["sha256"], "newBytes": new_path.stat().st_size,
                               "newSha256": sha(new_path), "gaps": gaps, "textures": changes})
        print(name, "dropped textures", len(changes), "saved", info["bytes"] - new_path.stat().st_size, flush=True)
    files = []
    for path in metadata:
        env = UnityPy.load(str(path))
        before = {obj.path_id: digest(obj.get_raw_data()) for obj in env.objects}
        changes, expected = [], {}
        try:
            for obj in env.objects:
                if obj.type.name not in STREAM_TYPES:
                    continue
                tree = obj.read_typetree()
                stream = tree.get("m_StreamData", {})
                physical = stream.get("path", "").removesuffix(".resource")
                if not stream.get("size") or physical not in remaps:
                    continue
                previous = copy.deepcopy(tree)
                old_offset, old_size = stream["offset"], stream["size"]
                stream["offset"], stream["size"] = map_range(old_offset, old_size, remaps[physical])
                row = selected.get((path.name, obj.path_id))
                if row:
                    assert previous["m_StreamData"] == row["stream"]
                    tree["m_Width"] = max(1, row["width"] >> row["droppedMips"])
                    tree["m_Height"] = max(1, row["height"] >> row["droppedMips"])
                    tree["m_MipCount"] -= row["droppedMips"]
                    tree["m_CompleteImageSize"] = row["newSize"]
                    allowed = {"m_Width", "m_Height", "m_MipCount", "m_CompleteImageSize", "m_StreamData"}
                else:
                    allowed = {"m_StreamData"}
                    assert stream["size"] == old_size
                    assert range_sha(staging / physical, old_offset, old_size) == range_sha(output / physical, stream["offset"], stream["size"]), "Unselected payload changed"
                assert {k: v for k, v in tree.items() if k not in allowed} == {k: v for k, v in previous.items() if k not in allowed}
                if tree == previous:
                    continue
                raw = obj.save_typetree(tree)
                expected[obj.path_id] = semantic_sha(tree)
                changes.append({"pathId": obj.path_id, "type": obj.type.name, "name": tree.get("m_Name"), "oldSha256": before[obj.path_id], "newSha256": digest(raw),
                    "oldStream": previous["m_StreamData"], "newStream": dict(stream), "mipDrop": bool(row), "allowedFields": sorted(allowed),
                    "oldSemanticSha256": semantic_sha(previous), "newSemanticSha256": semantic_sha(tree)})
            if not changes:
                continue
            destination = output / path.name
            destination.write_bytes(env.file.save())
            check = UnityPy.load(str(destination))
            try:
                objects = {obj.path_id: obj for obj in check.objects}
                assert objects.keys() == before.keys()
                altered = {r["pathId"]: r for r in changes}
                for key, obj in objects.items():
                    if key in altered:
                        row = altered[key]
                        raw = obj.get_raw_data()
                        assert digest(raw) == row["newSha256"] and semantic_sha(obj.read_typetree()) == expected[key]
                        export = output / "objects" / (path.name + "." + str(key) + "." + obj.type.name)
                        export.write_bytes(raw)
                        row["objectFile"] = str(export.relative_to(output))
                    else:
                        assert digest(obj.get_raw_data()) == before[key], "Unselected raw object changed"
            finally:
                check.file.reader.dispose()
            files.append({"file": path.name, "objects": changes, "unchangedRawObjectsVerified": len(before) - len(changes), "candidateSha256": sha(destination)})
            print(path.name, "mip drops", sum(r["mipDrop"] for r in changes), "total stream metadata edits", len(changes), flush=True)
        finally:
            env.file.reader.dispose()
    full, saved = sum(r["size"] for r in inventory), sum(r["skipBytes"] for r in selected.values())
    manifest = {"profile": "mobile_existing_mips", "maxDimension": cap, "input": str(staging), "selectedTextures": len(selected), "removedPayloadBytes": saved,
        "externalTexture2DFullPayloadBefore": full, "externalTexture2DFullPayloadAfter": full - saved, "allOriginalMipPayloadsReencoded": False,
        "allUnrelatedStreamBytesPreserved": True, "allAliasesAndStreamingFlagsPreserved": True, "protectionEvidence": "protected_texture_references.json",
        "protectedTextureCount": len(protected), "excludedOversizeTextures": excluded, "streams": stream_reports, "files": files,
        "limits": ["Payload arithmetic excludes driver alignment, copies and nontexture allocations", "Actual device GPU/RSS/VAS and appearance still require testing", "Originals and full-resolution work conversion remain intact"]}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(json.dumps({k: manifest[k] for k in ("selectedTextures", "removedPayloadBytes", "externalTexture2DFullPayloadBefore", "externalTexture2DFullPayloadAfter", "protectedTextureCount")}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--staging", type=Path, default=ROOT / "app/obb/assets/bin/Data")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-dimension", type=int, default=2048)
    parser.add_argument('--selection', type=Path, help='Exact reviewed texture identities and optional per-texture caps')
    args = parser.parse_args()
    prepare(args.staging, args.output, args.max_dimension, args.selection)
