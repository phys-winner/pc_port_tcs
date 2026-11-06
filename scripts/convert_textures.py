"""Repack unsupported PC texture payloads without rebuilding game logic or scenes."""
from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path

import astc_encoder as astc
import etcpak
import numpy as np
import texture2ddecoder
import UnityPy
from UnityPy.enums import BuildTarget, TextureFormat as TF
from UnityPy.export.Texture2DConverter import parse_image_data

PC_FORMATS = {10, 12, 24, 25, 26, 27}
STREAM_TYPES = {"Mesh", "Texture2D", "Cubemap", "Texture3D", "Texture2DArray"}


def sha(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def dump(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def semantic_sha(tree):
    return hashlib.sha256(json.dumps(tree, sort_keys=True, separators=(",", ":"), default=lambda value: list(value)).encode("utf-8")).hexdigest()


def mip_sizes(width, height, count, block, block_bytes):
    return [math.ceil(max(1, width >> mip) / block) * math.ceil(max(1, height >> mip) / block) * block_bytes for mip in range(count)]


def resolve_file(filename, file_id, externals):
    if not file_id:
        return filename
    name = externals[filename][file_id - 1].replace("\\", "/")
    return name if name.startswith("Resources/") else name.rsplit("/", 1)[-1]


def has_alpha(item, source):
    stream = item.get("m_StreamData", {})
    if not stream.get("size"):
        return True
    with (source / stream["path"]).open("rb") as original:
        original.seek(stream["offset"])
        for _ in range(item["m_ImageCount"]):
            sizes = mip_sizes(item["m_Width"], item["m_Height"], item["m_MipCount"], 4, 8 if item["m_TextureFormat"] in {TF.DXT1, TF.BC4} else 16)
            for mip, size in enumerate(sizes):
                width, height = max(1, item["m_Width"] >> mip), max(1, item["m_Height"] >> mip)
                pixels = parse_image_data(original.read(size), width, height, item["m_TextureFormat"], (2020, 3, 30, 1), BuildTarget.StandaloneWindows64, flip=False).convert("RGBA")
                if pixels.getextrema()[3] != (255, 255):
                    return True
    return False


def plan(args):
    inventory = json.loads(args.inventory.read_text(encoding="utf-8"))
    source = args.source.resolve()
    externals = {}
    files = {}
    cutout_shaders = set()
    for item in inventory["files"]:
        if item["name"].startswith("Resources"):
            continue
        path = source / item["name"]
        assert sha(path) == item["sha256"], f"Audited metadata changed: {path}"
        files[item["name"]] = {"bytes": path.stat().st_size, "sha256": item["sha256"]}
        env = UnityPy.load(str(path))
        serialized = next(iter(env.files.values()))
        externals[item["name"]] = [entry.path for entry in serialized.externals]
        for obj in env.objects:
            if obj.type.name == "Shader":
                shader = obj.read_typetree()["m_ParsedForm"]
                tags = [dict(sub.get("m_Tags", {}).get("tags", [])) for sub in shader["m_SubShaders"]]
                if any("AlphaTest" in tag.get("QUEUE", "") or "Cutout" in tag.get("RenderType", "") or tag.get("RenderType") == "TreeLeaf" for tag in tags):
                    cutout_shaders.add((item["name"], obj.path_id))
    cutout = set()
    custom_cutout = set()
    for material in inventory["materials"]:
        filename = material["file"]
        if filename not in externals:
            continue
        floats = dict(material["m_SavedProperties"].get("m_Floats", []))
        shader = material["m_Shader"]
        tagged = (resolve_file(filename, shader["m_FileID"], externals), shader["m_PathID"]) in cutout_shaders
        explicit = "_ALPHATEST_ON" in material.get("m_ShaderKeywords", "") or floats.get("_Mode") == 1
        if not explicit and not tagged:
            continue
        for name, entry in material["m_SavedProperties"].get("m_TexEnvs", []):
            if name != "_MainTex":
                continue
            pointer = entry["m_Texture"]
            if pointer["m_PathID"]:
                (cutout if explicit else custom_cutout).add((resolve_file(filename, pointer["m_FileID"], externals), pointer["m_PathID"]))
    textures = []
    for item in inventory["textures"]:
        if item["file"] not in files or item["m_TextureFormat"] not in PC_FORMATS:
            continue
        texture = dict(item)
        key = (item["file"], item["path_id"])
        if key in custom_cutout and key not in cutout and item["m_TextureFormat"] != TF.BC6H and has_alpha(item, source):
            cutout.add(key)
        target = TF.RGBAHalf if item["m_TextureFormat"] == TF.BC6H else TF.ETC2_RGBA8 if key in cutout else TF.ASTC_RGBA_4x4 if item["m_TextureFormat"] == TF.BC5 else TF.ASTC_RGBA_6x6
        texture["target_format"] = int(target)
        texture["target_name"] = target.name
        texture["reason"] = "preserve_HDR" if target == TF.RGBAHalf else "alpha_cutout" if target == TF.ETC2_RGBA8 else "BC5_normal_precision" if target == TF.ASTC_RGBA_4x4 else "measured_ASTC6"
        if item["m_TextureFormat"] == TF.BC5:
            texture["normal_packing"] = "DXT5nm_AG_R1"
        width, height, count, faces = (item[k] for k in ["m_Width", "m_Height", "m_MipCount", "m_ImageCount"])
        old_sizes = mip_sizes(width, height, count, 4, 8 if item["m_TextureFormat"] in {TF.DXT1, TF.BC4} else 16)
        texture["source_mip_bytes"] = old_sizes
        target_sizes = mip_sizes(width, height, count, 1, 8) if target == TF.RGBAHalf else mip_sizes(width, height, count, 4 if target in {TF.ETC2_RGBA8, TF.ASTC_RGBA_4x4} else 6, 16)
        texture["target_mip_bytes"] = target_sizes
        texture["target_bytes"] = sum(target_sizes) * faces
        stream = item.get("m_StreamData", {})
        original_size = stream.get("size", 0) or item["inline_bytes"]
        assert sum(old_sizes) * faces == original_size, f"Unexpected source mip layout: {key}"
        texture["source_bytes"] = original_size
        if stream.get("size"):
            name = stream["path"]
            assert Path(name).name == name, f"Unexpected stream path: {name}"
            if name not in files:
                path = source / name
                files[name] = {"bytes": path.stat().st_size, "sha256": sha(path)}
        textures.append(texture)
    result = {"source": str(source), "inventory_sha256": sha(args.inventory), "files": files, "textures": textures, "source_tree_bytes": inventory["total_bytes"]}
    result["estimated_tree_bytes"] = inventory["total_bytes"] + sum(t["target_bytes"] - t["source_bytes"] for t in textures)
    dump(args.manifest, result)
    print(json.dumps({"textures": len(textures), "cutout_textures": sum(t["reason"] == "alpha_cutout" for t in textures), "estimated_tree_bytes": result["estimated_tree_bytes"]}), flush=True)


def encode_texture(item, raw, bc6_dir, contexts):
    assert len(raw) == item["source_bytes"]
    if item["target_format"] == TF.RGBAHalf:
        path = bc6_dir / f"{item['file']}_{item['path_id']}.rgba16f"
        result = path.read_bytes()
        assert len(result) == item["target_bytes"], f"HDR decode missing/truncated: {path}"
        values = np.frombuffer(result, dtype="<f2")
        assert np.isfinite(values).all(), f"Nonfinite HDR decode: {path}"
        return result, {"hdr_min": float(values.reshape(-1, 4)[:, :3].min()), "hdr_max": float(values.reshape(-1, 4)[:, :3].max()), "decoded_hdr_sha256": hashlib.sha256(result).hexdigest()}
    output = bytearray()
    position = 0
    top_error = None
    for face in range(item["m_ImageCount"]):
        for mip, size in enumerate(item["source_mip_bytes"]):
            width, height = max(1, item["m_Width"] >> mip), max(1, item["m_Height"] >> mip)
            pixels = np.asarray(parse_image_data(raw[position:position + size], width, height, item["m_TextureFormat"], (2020, 3, 30, 1), BuildTarget.StandaloneWindows64, flip=False).convert("RGBA")).copy()
            position += size
            if item["m_TextureFormat"] == TF.BC5:
                # R=1 preserves both D3D RG-or-AG and Android ASTC AG decoding.
                x, y = pixels[..., 0].copy(), pixels[..., 1].copy()
                pixels[..., 0] = 255
                pixels[..., 2] = 255
                pixels[..., 3] = x
            if item["target_format"] == TF.ETC2_RGBA8:
                padded_width, padded_height = max(4, math.ceil(width / 4) * 4), max(4, math.ceil(height / 4) * 4)
                padded = np.pad(pixels, ((0, padded_height - height), (0, padded_width - width), (0, 0)), mode="edge")
                encoded = etcpak.compress_etc2_rgba(padded.tobytes(), padded_width, padded_height)
                decoded = np.frombuffer(texture2ddecoder.decode_etc2a8(encoded, width, height), np.uint8).reshape(height, width, 4)[..., [2, 1, 0, 3]]
            else:
                profile = astc.ASTCProfile.LDR_SRGB if item["m_ColorSpace"] else astc.ASTCProfile.LDR
                block = 4 if item["target_format"] == TF.ASTC_RGBA_4x4 else 6
                context_key = (profile, block)
                if context_key not in contexts:
                    contexts[context_key] = astc.ASTCContext(astc.ASTCConfig(profile, block, block, quality=astc.ASTCQualityPreset.MEDIUM), threads=4)
                context = contexts[context_key]
                encoded = context.compress(astc.ASTCImage(astc.ASTCType.U8, width, height, data=pixels.tobytes()), astc.ASTCSwizzle())
                decoded = np.frombuffer(texture2ddecoder.decode_astc(encoded, width, height, block, block), np.uint8).reshape(height, width, 4)[..., [2, 1, 0, 3]]
            assert len(encoded) == item["target_mip_bytes"][mip]
            assert decoded.shape == pixels.shape
            if not face and not mip:
                error = np.mean((pixels.astype(np.float64) - decoded.astype(np.float64)) ** 2, axis=(0, 1))
                top_error = {"top_mip_mse_rgba": error.tolist()}
            output.extend(encoded)
    assert position == len(raw) and len(output) == item["target_bytes"]
    if item["m_TextureFormat"] == TF.BC5:
        top_error["normal_packing"] = "DXT5nm_AG_R1"
    return bytes(output), top_error


def copy_range(source, target, size):
    digest = hashlib.sha256()
    while size:
        data = source.read(min(size, 1024 * 1024))
        assert data, "Truncated source stream"
        digest.update(data)
        target.write(data)
        size -= len(data)
    return digest.hexdigest()


def range_sha(path, offset, size):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        source.seek(offset)
        while size:
            data = source.read(min(size, 1024 * 1024))
            assert data
            digest.update(data)
            size -= len(data)
    return digest.hexdigest()


def reuse_stream(filename, textures, source, output, previous):
    records = [record for record in previous if record.get("stream") == filename]
    destination = output / filename
    gap_file = output / (filename + ".gaps.json")
    if not destination.exists() or not gap_file.exists() or len(records) != len(textures):
        return None
    records.sort(key=lambda record: record["old_offset"])
    textures = sorted(textures, key=lambda texture: texture["m_StreamData"]["offset"])
    if any((record["file"], record["path_id"], record["new_format"], record.get("normal_packing")) != (texture["file"], texture["path_id"], texture["target_name"], texture.get("normal_packing")) for texture, record in zip(textures, records)):
        return None
    gaps = json.loads(gap_file.read_text(encoding="utf-8"))
    assert len(gaps) == len(records) + 1
    old_cursor = new_cursor = 0
    remaps = []
    for texture, record, gap in zip(textures, records, gaps):
        stream = texture["m_StreamData"]
        assert (record["file"], record["path_id"], record["new_format"]) == (texture["file"], texture["path_id"], texture["target_name"]), "Cached conversion profile differs"
        assert (record["old_offset"], record["old_bytes"], record["new_bytes"]) == (stream["offset"], stream["size"], texture["target_bytes"])
        assert (gap["old_offset"], gap["new_offset"], gap["bytes"]) == (old_cursor, new_cursor, stream["offset"] - old_cursor)
        assert range_sha(source / filename, old_cursor, gap["bytes"]) == gap["sha256"] == range_sha(destination, new_cursor, gap["bytes"])
        new_cursor += gap["bytes"]
        assert record["new_offset"] == new_cursor
        assert range_sha(source / filename, stream["offset"], stream["size"]) == record["source_payload_sha256"]
        assert range_sha(destination, new_cursor, texture["target_bytes"]) == record["output_payload_sha256"]
        remaps.append((stream["offset"], stream["size"], new_cursor, texture["target_bytes"]))
        old_cursor = stream["offset"] + stream["size"]
        new_cursor += texture["target_bytes"]
    gap = gaps[-1]
    assert (gap["old_offset"], gap["new_offset"], gap["bytes"]) == (old_cursor, new_cursor, (source / filename).stat().st_size - old_cursor)
    assert range_sha(source / filename, old_cursor, gap["bytes"]) == gap["sha256"] == range_sha(destination, new_cursor, gap["bytes"])
    assert destination.stat().st_size == new_cursor + gap["bytes"]
    return records, remaps


def convert(args):
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    source = Path(manifest["source"]).resolve()
    output = args.out.resolve()
    assert source != output and source not in output.parents, "Output must be outside original input"
    hdr_max = 0.0
    for item in manifest["textures"]:
        if item["target_format"] != TF.RGBAHalf:
            continue
        data = (args.bc6_dir / f"{item['file']}_{item['path_id']}.rgba16f").read_bytes()
        assert len(data) == item["target_bytes"], "Incomplete HDR mip/face decode"
        values = np.frombuffer(data, dtype="<f2").reshape(-1, 4)
        assert np.isfinite(values).all(), "Nonfinite decoded HDR"
        hdr_max = max(hdr_max, float(values[:, :3].max()))
    if any(t["target_format"] == TF.RGBAHalf for t in manifest["textures"]):
        assert hdr_max > 1.0, "HDR decoder unexpectedly clamped all RGB values"
        print("HDR_FLOAT_RANGE_VERIFIED", hdr_max, flush=True)
    output.mkdir(parents=True, exist_ok=True)
    for filename, info in manifest["files"].items():
        path = source / filename
        assert path.stat().st_size == info["bytes"] and sha(path) == info["sha256"], f"Source preimage mismatch: {filename}"
    print("SOURCE_PREIMAGES_VERIFIED", flush=True)
    contexts = {}
    records = []
    remaps = defaultdict(list)
    patches = defaultdict(list)
    for texture in manifest["textures"]:
        stream = texture.get("m_StreamData", {})
        if stream.get("size"):
            patches[stream["path"]].append(texture)
    progress_path = output / "conversion_progress.json"
    previous = json.loads(progress_path.read_text(encoding="utf-8")) if progress_path.exists() else []
    previous_textures = {(record["file"], record["path_id"]): record for record in previous}
    for filename, textures in patches.items():
        reused = reuse_stream(filename, textures, source, output, previous)
        if reused:
            records.extend(reused[0])
            remaps[filename] = reused[1]
            print("STREAM_RESUMED_VERIFIED", filename, len(reused[0]), flush=True)
            continue
        temporary = output / (filename + ".tmp")
        cursor = 0
        gaps = []
        with (source / filename).open("rb") as original, temporary.open("wb") as target:
            for item in sorted(textures, key=lambda t: t["m_StreamData"]["offset"]):
                stream = item["m_StreamData"]
                offset, size = stream["offset"], stream["size"]
                assert offset >= cursor, f"Overlapping source texture streams: {filename} {item['path_id']}"
                new_gap_offset = target.tell()
                gap_hash = copy_range(original, target, offset - cursor)
                gaps.append({"old_offset": cursor, "new_offset": new_gap_offset, "bytes": offset - cursor, "sha256": gap_hash})
                raw = original.read(size)
                assert len(raw) == size
                old = previous_textures.get((item["file"], item["path_id"]))
                if old and old.get("stream") == filename and old["new_format"] == item["target_name"] and old.get("normal_packing") == item.get("normal_packing") and old["source_payload_sha256"] == hashlib.sha256(raw).hexdigest() and (output / filename).exists():
                    with (output / filename).open("rb") as cached:
                        cached.seek(old["new_offset"])
                        replacement = cached.read(old["new_bytes"])
                    assert len(replacement) == item["target_bytes"] and hashlib.sha256(replacement).hexdigest() == old["output_payload_sha256"], "Cached texture payload changed"
                    quality = {key: old[key] for key in ["top_mip_mse_rgba", "hdr_min", "hdr_max", "decoded_hdr_sha256", "normal_packing"] if key in old}
                else:
                    replacement, quality = encode_texture(item, raw, args.bc6_dir, contexts)
                new_offset = target.tell()
                target.write(replacement)
                remaps[filename].append((offset, size, new_offset, len(replacement)))
                records.append({"file": item["file"], "path_id": item["path_id"], "name": item["m_Name"], "old_format": item["format"], "new_format": item["target_name"], "stream": filename, "old_offset": offset, "old_bytes": size, "new_offset": new_offset, "new_bytes": len(replacement), "source_payload_sha256": hashlib.sha256(raw).hexdigest(), "output_payload_sha256": hashlib.sha256(replacement).hexdigest(), **quality})
                cursor = offset + size
                if len(records) % 25 == 0:
                    print("CONVERTED", len(records), "/", len(manifest["textures"]), item["m_Name"], flush=True)
            count = (source / filename).stat().st_size - cursor
            new_offset = target.tell()
            gap_hash = copy_range(original, target, count)
            gaps.append({"old_offset": cursor, "new_offset": new_offset, "bytes": count, "sha256": gap_hash})
        temporary.replace(output / filename)
        with (output / filename).open("rb") as check:
            for gap in gaps:
                check.seek(gap["new_offset"])
                digest = hashlib.sha256()
                remaining = gap["bytes"]
                while remaining:
                    data = check.read(min(remaining, 1024 * 1024))
                    assert data
                    digest.update(data)
                    remaining -= len(data)
                assert digest.hexdigest() == gap["sha256"], "Nontexture stream changed"
        dump(output / (filename + ".gaps.json"), gaps)
        dump(output / "conversion_progress.json", records)
        print("STREAM_VERIFIED", filename, (output / filename).stat().st_size, flush=True)

    texture_map = {(t["file"], t["path_id"]): t for t in manifest["textures"]}
    changed_objects = []
    for filename in manifest["files"]:
        if filename.endswith(".resS"):
            continue
        env = UnityPy.load(str(source / filename))
        serialized = next(iter(env.files.values()))
        raw_hashes = {obj.path_id: hashlib.sha256(obj.get_raw_data()).hexdigest() for obj in env.objects}
        changed = set()
        expected_trees = {}
        for obj in env.objects:
            if obj.type.name not in STREAM_TYPES:
                assert b".resS" not in obj.get_raw_data(), f"Unrecognized stream-reference class: {filename} {obj.type.name} {obj.path_id}"
                continue
            tree = obj.read_typetree()
            stream = tree.get("m_StreamData", {})
            item = texture_map.get((filename, obj.path_id))
            modified = False
            if item:
                assert tree["m_TextureFormat"] == item["m_TextureFormat"] and tree["m_Width"] == item["m_Width"] and tree["m_Height"] == item["m_Height"] and tree["m_MipCount"] == item["m_MipCount"], "Texture metadata changed"
                tree["m_TextureFormat"] = item["target_format"]
                tree["m_CompleteImageSize"] = sum(item["target_mip_bytes"])
                if not stream.get("size"):
                    raw = bytes(tree["image data"])
                    encoded, quality = encode_texture(item, raw, args.bc6_dir, contexts)
                    tree["image data"] = encoded
                    records.append({"file": filename, "path_id": obj.path_id, "name": item["m_Name"], "old_format": item["format"], "new_format": item["target_name"], "old_bytes": len(raw), "new_bytes": len(encoded), "source_payload_sha256": hashlib.sha256(raw).hexdigest(), "output_payload_sha256": hashlib.sha256(encoded).hexdigest(), **quality})
                modified = True
            if stream.get("size") and stream["path"] in remaps:
                old_offset, old_size = stream["offset"], stream["size"]
                shift = 0
                for offset, size, new_offset, new_size in remaps[stream["path"]]:
                    if old_offset == offset and old_size == size:
                        stream["offset"], stream["size"] = new_offset, new_size
                        modified = True
                        break
                    assert old_offset + old_size <= offset or old_offset >= offset + size, "Nontexture reference overlaps replacement"
                    if old_offset >= offset + size:
                        shift += new_size - size
                else:
                    if shift:
                        stream["offset"] = old_offset + shift
                        modified = True
            if modified:
                obj.save_typetree(tree)
                changed.add(obj.path_id)
                expected_trees[obj.path_id] = semantic_sha(tree)
        target = output / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_suffix(target.suffix + ".tmp")
        temporary.write_bytes(serialized.save())
        temporary.replace(target)
        check = UnityPy.load(str(target))
        assert set(raw_hashes) == {obj.path_id for obj in check.objects}, "Object identities changed"
        for obj in check.objects:
            if obj.path_id not in changed:
                assert hashlib.sha256(obj.get_raw_data()).hexdigest() == raw_hashes[obj.path_id], f"Unrelated object changed: {filename} {obj.path_id}"
            else:
                assert semantic_sha(obj.read_typetree()) == expected_trees[obj.path_id], f"Changed object properties/PPtrs differ: {filename} {obj.path_id}"
        changed_objects.append({"file": filename, "changed_path_ids": sorted(changed), "unchanged_raw_objects_verified": len(raw_hashes) - len(changed), "sha256": sha(target)})
        print("SERIALIZED_VERIFIED", filename, len(changed), flush=True)
    assert len(records) == len(manifest["textures"]), "Texture conversion count mismatch"
    for name, info in manifest["files"].items():
        assert sha(source / name) == info["sha256"], f"Original changed during conversion: {name}"
    summary = {"source_manifest_sha256": sha(args.manifest), "textures": records, "serialized": changed_objects, "output_files": {p.name: {"bytes": p.stat().st_size, "sha256": sha(p)} for p in output.iterdir() if p.is_file() and p.suffix not in {".json", ".tmp"}}, "limitations": ["Target shader normal encoding must read AG (ASTC), not RGB", "Only source mip dimensions, counts and offline decode were verified; Android rendering remains unverified", "Package large resources in splits if final APK reaches ZIP32 limit"]}
    dump(output / "conversion_manifest.json", summary)
    print("TEXTURE_CONVERSION_VERIFIED", len(records), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prepare = sub.add_parser("plan")
    prepare.add_argument("--source", type=Path, required=True)
    prepare.add_argument("--inventory", type=Path, required=True)
    prepare.add_argument("--manifest", type=Path, required=True)
    run = sub.add_parser("convert")
    run.add_argument("--manifest", type=Path, required=True)
    run.add_argument("--out", type=Path, required=True)
    run.add_argument("--bc6-dir", type=Path, required=True)
    args = parser.parse_args()
    (plan if args.command == "plan" else convert)(args)


if __name__ == "__main__":
    main()
