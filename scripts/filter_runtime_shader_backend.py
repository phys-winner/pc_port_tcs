"""Prepare Android-runtime Shader copies without the unusable D3D11 backend."""
from collections import Counter
from pathlib import Path
import argparse
import copy
import hashlib
import json
import struct
import lz4.block
import UnityPy

ROOT = Path(__file__).resolve().parents[1]
STAGES = ("progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing")
D3D_TYPES = set(range(13, 23))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def program_snapshot(tree, platform):
    index = tree["platforms"].index(platform)
    segments = []
    for offset, size, raw_size in zip(tree["offsets"][index], tree["compressedLengths"][index], tree["decompressedLengths"][index]):
        raw = lz4.block.decompress(bytes(tree["compressedBlob"])[offset:offset + size], uncompressed_size=raw_size)
        assert len(raw) == raw_size
        segments.append(raw)
    assert segments and len(segments[0]) >= 4
    count = struct.unpack_from("<I", segments[0])[0]
    assert 4 + 12 * count <= len(segments[0])
    records, types = [], Counter()
    for i in range(count):
        offset, size, segment = struct.unpack_from("<III", segments[0], 4 + 12 * i)
        assert segment < len(segments) and offset + size <= len(segments[segment]) and size >= 24
        record = segments[segment][offset:offset + size]
        records.append(digest(record))
        types[struct.unpack_from("<I", record, 4)[0]] += 1
    return {"records": records, "types": {str(key): count for key, count in types.items()}, "segmentHashes": list(map(digest, segments)), "decompressedBytes": sum(map(len, segments))}


def filtered(tree):
    assert 4 in tree["platforms"] and len(set(tree["platforms"])) == len(tree["platforms"])
    original = tree
    target = copy.deepcopy(tree)
    count = len(tree["platforms"])
    assert all(len(tree[key]) == count for key in ("offsets", "compressedLengths", "decompressedLengths"))
    kept = [i for i, p in enumerate(tree["platforms"]) if p != 4]
    all_ranges, keep_ranges = {}, set()
    for platform_index in range(count):
        arrays = [tree[key][platform_index] for key in ("offsets", "compressedLengths", "decompressedLengths")]
        assert len({len(a) for a in arrays}) == 1
        for offset, length, raw_length in zip(*arrays):
            assert min(offset, length, raw_length) >= 0 and offset + length <= len(tree["compressedBlob"])
            span = (offset, length)
            assert span not in all_ranges or all_ranges[span] == raw_length
            all_ranges[span] = raw_length
            if platform_index in kept:
                keep_ranges.add(span)
    # No unknown compressedBlob bytes or partially overlapping segments may disappear.
    cursor = 0
    for offset, length in sorted(all_ranges):
        assert offset == cursor, "Unowned or overlapping compressed segment bytes"
        cursor += length
    assert cursor == len(tree["compressedBlob"])
    blob, rebased = bytearray(), {}
    for offset, length in sorted(keep_ranges):
        rebased[(offset, length)] = len(blob)
        blob.extend(bytes(tree["compressedBlob"])[offset:offset + length])
    target["platforms"] = [tree["platforms"][i] for i in kept]
    target["offsets"] = [[rebased[(offset, length)] for offset, length in zip(tree["offsets"][i], tree["compressedLengths"][i])] for i in kept]
    for key in ("compressedLengths", "decompressedLengths"):
        target[key] = [copy.deepcopy(tree[key][i]) for i in kept]
    target["compressedBlob"] = list(blob)
    removed = Counter()
    cleared_common = 0
    for before_sub, after_sub in zip(original["m_ParsedForm"]["m_SubShaders"], target["m_ParsedForm"]["m_SubShaders"]):
        for before_pass, after_pass in zip(before_sub["m_Passes"], after_sub["m_Passes"]):
            assert len(before_pass["m_Platforms"]) == len(before_pass["m_EditorDataHash"]), "Ambiguous pass platform/hash correspondence"
            pass_keep = [i for i, p in enumerate(before_pass["m_Platforms"]) if p != 4]
            for key in ("m_Platforms", "m_EditorDataHash"):
                after_pass[key] = [copy.deepcopy(before_pass[key][i]) for i in pass_keep]
            for stage in STAGES:
                if stage not in before_pass:
                    continue
                before_program, after_program = before_pass[stage], after_pass[stage]
                rejected = [sp for sp in before_program["m_SubPrograms"] if sp["m_GpuProgramType"] in D3D_TYPES]
                after_program["m_SubPrograms"] = [copy.deepcopy(sp) for sp in before_program["m_SubPrograms"] if sp["m_GpuProgramType"] not in D3D_TYPES]
                removed.update(sp["m_GpuProgramType"] for sp in rejected)
                if rejected and not after_program["m_SubPrograms"]:
                    common = after_program["m_CommonParameters"]
                    assert all(isinstance(v, list) for v in common.values())
                    cleared_common += sum(map(len, common.values()))
                    after_program["m_CommonParameters"] = {key: [] for key in common}
    # Normalize target differences back to source and compare every author field.
    restored = copy.deepcopy(target)
    for key in ("platforms", "offsets", "compressedLengths", "decompressedLengths", "compressedBlob"):
        restored[key] = original[key]
    for a, b in zip(original["m_ParsedForm"]["m_SubShaders"], restored["m_ParsedForm"]["m_SubShaders"]):
        for pa, pb in zip(a["m_Passes"], b["m_Passes"]):
            for key in ("m_Platforms", "m_EditorDataHash"):
                pb[key] = pa[key]
            for stage in STAGES:
                if stage not in pa:
                    continue
                pb[stage] = pa[stage]
    assert restored == original, "A field outside backend program data changed"
    for before_sub, after_sub in zip(original["m_ParsedForm"]["m_SubShaders"], target["m_ParsedForm"]["m_SubShaders"]):
        for before_pass, after_pass in zip(before_sub["m_Passes"], after_sub["m_Passes"]):
            for stage in STAGES:
                if stage not in before_pass:
                    continue
                expected = [sp for sp in before_pass[stage]["m_SubPrograms"] if sp["m_GpuProgramType"] not in D3D_TYPES]
                assert after_pass[stage]["m_SubPrograms"] == expected, "A kept backend subprogram changed"
                if expected:
                    assert before_pass[stage]["m_CommonParameters"] == after_pass[stage]["m_CommonParameters"]
    return target, {"removedSubPrograms": sum(removed.values()), "removedTypes": dict(removed), "clearedUnusedCommonEntries": cleared_common,
        "removedCompressedBytes": len(original["compressedBlob"]) - len(blob)}


def prepare(app, output):
    assert output.resolve().is_relative_to(ROOT / "work")
    assert not (output / "manifest.json").exists(), "Choose a fresh output; preserve completed evidence"
    output.mkdir(parents=True, exist_ok=True)
    (output / "objects").mkdir(exist_ok=True)
    manifest = {"inputApp": str(app.resolve()), "removedCompilerPlatform": 4, "removedGpuProgramTypes": sorted(D3D_TYPES),
        "scope": "Runtime-only copies; original C:/ game and converted work artifacts remain full-backend inputs",
        "runtimeMemoryMeasured": False, "files": []}
    paths = [app / "obb/assets/bin/Data" / name for name in ("resources.assets", "sharedassets0.assets", "sharedassets1.assets")]
    paths += [app / "assets/bin/Data" / name for name in ("globalgamemanagers.assets", "unity default resources", "Resources/unity default resources", "Resources/unity_builtin_extra")]
    for path in paths:
        if not path.is_file():
            continue
        preimage = file_digest(path)
        env = UnityPy.load(str(path))
        before = {obj.path_id: digest(obj.get_raw_data()) for obj in env.objects}
        rows, expected_trees = [], {}
        for obj in env.objects:
            if obj.type.name != "Shader":
                continue
            tree = obj.read_typetree()
            if 4 not in tree["platforms"]:
                continue
            source_snapshots = {p: program_snapshot(tree, p) for p in tree["platforms"]}
            assert set(map(int, source_snapshots[4]["types"])) <= D3D_TYPES, "Unexpected GPU type inside D3D11 platform"
            assert not any(D3D_TYPES.intersection(map(int, s["types"])) for p, s in source_snapshots.items() if p != 4), "D3D types shared with a kept compiler platform"
            target, stats = filtered(tree)
            expected_trees[obj.path_id] = target
            raw = obj.save_typetree(target)
            row = {"pathId": obj.path_id, "name": tree["m_ParsedForm"]["m_Name"], "oldSha256": before[obj.path_id], "newSha256": digest(raw),
                "oldBytes": len(obj.get_raw_data()), "newBytes": len(raw), "oldPlatforms": tree["platforms"], "newPlatforms": target["platforms"],
                "removedD3DRecords": len(source_snapshots[4]["records"]), "removedD3DDecompressedBytes": source_snapshots[4]["decompressedBytes"],
                "keptSnapshots": {str(p): s for p, s in source_snapshots.items() if p != 4}, **stats}
            rows.append(row)
        assert file_digest(path) == preimage, "Input changed concurrently"
        if not rows:
            env.file.reader.dispose()
            continue
        relative = path.relative_to(app).as_posix()
        candidate = output / "containers" / relative
        candidate.parent.mkdir(parents=True, exist_ok=True)
        candidate.write_bytes(env.file.save())
        env.file.reader.dispose()
        check = UnityPy.load(str(candidate))
        changed = {row["pathId"]: row for row in rows}
        objects = {obj.path_id: obj for obj in check.objects}
        assert set(objects) == set(before)
        for key, obj in objects.items():
            raw = obj.get_raw_data()
            if key not in changed:
                assert digest(raw) == before[key], "Unrelated object changed"
                continue
            row = changed[key]
            assert digest(raw) == row["newSha256"]
            tree = obj.read_typetree()
            assert tree == expected_trees[key], "Persisted author fields/kept subprogram metadata differ from verified target"
            assert tree["platforms"] == row["newPlatforms"] and 4 not in tree["platforms"]
            for p, expected in row["keptSnapshots"].items():
                assert program_snapshot(tree, int(p)) == expected, "Kept backend segment/record bytes changed"
            export = output / "objects" / (relative.replace("/", "__") + "." + str(key) + ".shader")
            export.write_bytes(raw)
            row["objectFile"] = str(export.relative_to(output))
            row["allKeptRecordsAndSegmentsVerified"] = True
        check.file.reader.dispose()
        manifest["files"].append({"file": relative, "sourceSha256": preimage, "candidateSha256": file_digest(candidate),
            "oldFileBytes": path.stat().st_size, "newFileBytes": candidate.stat().st_size, "unchangedObjectsVerified": len(before) - len(rows), "shaders": rows})
        print(relative, "shaders", len(rows), "serialized bytes saved", path.stat().st_size - candidate.stat().st_size, flush=True)
    shaders = [s for f in manifest["files"] for s in f["shaders"]]
    manifest["summary"] = {"changedShaders": len(shaders), "removedSubPrograms": sum(s["removedSubPrograms"] for s in shaders),
        "removedCompressedBytes": sum(s["removedCompressedBytes"] for s in shaders), "serializedObjectBytesSaved": sum(s["oldBytes"] - s["newBytes"] for s in shaders),
        "removedD3DDecompressedBytes": sum(s["removedD3DDecompressedBytes"] for s in shaders)}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(json.dumps(manifest["summary"], indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-app", type=Path, default=ROOT / "app")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    prepare(args.input_app, args.output)
