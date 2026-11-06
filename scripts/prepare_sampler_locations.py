"""Pin GLES sampler locations to native Unity's texture-unit ABI; work-only candidate."""
from pathlib import Path
from collections import Counter
import argparse
import copy
import hashlib
import json
import os
import re
import subprocess
import lz4.block
import UnityPy
from unity_shader_blob import read_programs, encode_record
from convert_shaders import encode_segments
from filter_runtime_shader_backend import program_snapshot

ROOT = Path(__file__).resolve().parents[1]
VALIDATOR = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/emulator/lib64/vulkan/glslangValidator.exe'
SAMPLER = re.compile(r"(?m)^(?P<indent>[ \t]*)(?P<location>(?:UNITY_LOCATION\(\s*(?P<macro>\d+)\s*\)|layout\(\s*location\s*=\s*(?P<layout>\d+)\s*\))\s+)?(?P<declaration>uniform\s+(?:(?:lowp|mediump|highp)\s+)?(?P<type>[iu]?sampler\w+)\s+(?P<name>\w+)\s*(?:\[\s*(?P<count>\d+)\s*\])?\s*;)")


def digest(data): return hashlib.sha256(data).hexdigest()


def patch_code(code):
    text = code.decode("utf-8")
    declarations = list(SAMPLER.finditer(text))
    if not declarations:
        return code, {}, None
    # Original builtin ES300 programs already follow their own Unity macros;
    # only newly translated ES310 records are changed here.
    if not re.search(r"(?m)^#version 310 es\s*$", text):
        return code, {}, None
    assert text.count("#define UNITY_LOCATION(x)") >= 2, "Unity's runtime location macro absent"
    assert len(re.findall(r"\buniform\s+(?:(?:lowp|mediump|highp)\s+)?[iu]?sampler\w+\b", text)) == len(declarations), "Unparsed sampler declaration"
    names, occupied = {}, {}
    for match in declarations:
        name, kind, count = match["name"], match["type"], int(match["count"] or 1)
        assert count == 1, "Sampler arrays need an explicit contiguous range audit"
        if name in names:
            assert names[name]["type"] == kind, "Vertex/fragment sampler types differ"
        else:
            names[name] = {"type": kind, "location": None}
        if match["location"]:
            unit = int(match["macro"] or match["layout"])
            assert 0 <= unit < 32
            assert unit not in occupied or occupied[unit] == name, "Existing sampler locations overlap"
            assert names[name]["location"] in (None, unit)
            names[name]["location"] = unit
            occupied[unit] = name
    for name, entry in names.items():
        if entry["location"] is None:
            unit = next((i for i in range(32) if i not in occupied), None)
            assert unit is not None, "Combined vertex/fragment samplers exceed native Unity's32 units"
            entry["location"] = unit
            occupied[unit] = name
    changes = 0
    def replace(match):
        nonlocal changes
        if match["location"]:
            return match.group(0)
        changes += 1
        return match["indent"] + "UNITY_LOCATION(" + str(names[match["name"]]["location"]) + ") " + match["declaration"]
    patched = SAMPLER.sub(replace, text)
    if not changes:
        return code, names, None
    # Reversing only the injected prefixes must recover the exact source text.
    original_iter = iter(declarations)
    def strip(match):
        previous = next(original_iter)
        return match.group(0) if previous["location"] else match["indent"] + match["declaration"]
    assert SAMPLER.sub(strip, patched) == text
    assert len(list(SAMPLER.finditer(patched))) == len(declarations)
    assert all(m["location"] for m in SAMPLER.finditer(patched))
    signature = tuple((name, entry["type"], entry["location"]) for name, entry in names.items())
    return patched.encode("utf-8"), names, signature


def split_stages(text):
    vertex = "#ifdef VERTEX\n"
    fragment = "#ifdef FRAGMENT\n"
    assert text.startswith(vertex) and text.count(fragment) == 1
    left, right = text[len(vertex):].split(fragment)
    assert left.rstrip().endswith("#endif") and right.rstrip().endswith("#endif")
    return left.rstrip()[:-len("#endif")].rstrip() + "\n", right.rstrip()[:-len("#endif")].rstrip() + "\n"


def validate_layout(folder, code, signature, name, seen):
    vertex, fragment = split_stages(code.decode("utf-8"))
    # Shared sampler maps and stage membership determine the new location ABI.
    stage_names = tuple(tuple(m["name"] for m in SAMPLER.finditer(stage)) for stage in (vertex, fragment))
    key = name, signature, stage_names, bool("layout(std140) uniform" in vertex)
    if key in seen: return
    identity = digest(repr(key).encode())
    path = folder / identity
    path.mkdir(parents=True, exist_ok=True)
    v, f = path / "shader.vert", path / "shader.frag"
    v.write_text(vertex)
    f.write_text(fragment)
    run = subprocess.run([str(VALIDATOR), "-l", str(v), str(f)], capture_output=True, text=True)
    (path / "link.log").write_text(run.stdout + run.stderr)
    assert run.returncode == 0, "Patched GLES link failed: " + str(path)
    seen.add(key)


def replace_backend(tree, records):
    target = copy.deepcopy(tree)
    index = tree["platforms"].index(9)
    replacement = encode_segments(records)
    blob, offsets, sizes, raw_sizes = bytearray(), [], [], []
    for i, platform in enumerate(tree["platforms"]):
        a, b, c = [], [], []
        if i == index:
            segments = [(lz4.block.compress(raw, store_size=False), len(raw)) for raw in replacement]
        else:
            segments = [(bytes(tree["compressedBlob"])[off:off + size], raw) for off, size, raw in
                        zip(tree["offsets"][i], tree["compressedLengths"][i], tree["decompressedLengths"][i])]
        for packed, raw_size in segments:
            a.append(len(blob)); b.append(len(packed)); c.append(raw_size)
            blob.extend(packed)
        offsets.append(a); sizes.append(b); raw_sizes.append(c)
    target["compressedBlob"] = list(blob)
    target["offsets"], target["compressedLengths"], target["decompressedLengths"] = offsets, sizes, raw_sizes
    assert {k: v for k, v in target.items() if k not in ("compressedBlob", "offsets", "compressedLengths", "decompressedLengths")} == {
        k: v for k, v in tree.items() if k not in ("compressedBlob", "offsets", "compressedLengths", "decompressedLengths")}
    return target


def prepare(output):
    output = output.resolve()
    assert output.is_relative_to(ROOT / "work") and not (output / "manifest.json").exists()
    assert VALIDATOR.is_file()
    output.mkdir(parents=True, exist_ok=True)
    (output / "objects").mkdir(exist_ok=True)
    inputs = [ROOT / "app/obb/assets/bin/Data" / name for name in ("resources.assets", "sharedassets0.assets", "sharedassets1.assets")]
    inputs += [p for p in (ROOT / "app/assets/bin/Data/Resources").iterdir() if p.is_file() and not p.suffix]
    manifest = {"purpose": "Unity GLES sampler uniform-location equals texture-unit ABI", "nativeSamplerUnitCap": 32,
                "modifiedCode": "Inject UNITY_LOCATION(n) only before ES310 sampler declarations; same symbol uses same unit in both stages",
                "files": []}
    validated = set()
    for path in inputs:
        env = UnityPy.load(str(path))
        before = {obj.path_id: digest(obj.get_raw_data()) for obj in env.objects}
        rows, expected = [], {}
        try:
            for obj in env.objects:
                if obj.type.name != "Shader": continue
                tree = obj.read_typetree()
                if 9 not in tree["platforms"]: continue
                records = read_programs(tree, 9)
                encoded, changes = [], []
                name = tree["m_ParsedForm"]["m_Name"]
                for record in records:
                    assert encode_record(record) == record["record_bytes"], "Source shader record roundtrip differs"
                    code, names, signature = patch_code(record["program_code"])
                    if code == record["program_code"]:
                        encoded.append(record["record_bytes"])
                        continue
                    assert record["program_type"] == 4, "Unreviewed program type"
                    validate_layout(output / "validated_layouts", code, signature, name, validated)
                    old = record["program_code"]
                    record["program_code"] = code
                    raw = encode_record(record)
                    # All trailing bindings, keywords and stats remain exact.
                    changes.append({"record": record["index"], "oldCodeSha256": digest(old), "newCodeSha256": digest(code),
                                    "tailSha256": digest(record["tail_bytes"]), "samplers": names, "newRecordSha256": digest(raw)})
                    encoded.append(raw)
                if not changes: continue
                target = replace_backend(tree, encoded)
                other = {str(p): program_snapshot(tree, p) for p in tree["platforms"] if p != 9}
                for p, snapshot in other.items(): assert program_snapshot(target, int(p)) == snapshot
                raw = obj.save_typetree(target)
                rows.append({"pathId": obj.path_id, "name": name, "oldSha256": before[obj.path_id], "newSha256": digest(raw),
                             "recordCount": len(records), "changedRecords": changes, "otherBackends": other,
                             "glesSnapshot": program_snapshot(target, 9)})
                expected[obj.path_id] = target
                print(path.name, obj.path_id, name, "patched", len(changes), "/", len(records), flush=True)
            if not rows: continue
            relative = path.relative_to(ROOT / "app")
            candidate = output / relative
            candidate.parent.mkdir(parents=True, exist_ok=True)
            candidate.write_bytes(env.file.save())
            check = UnityPy.load(candidate.read_bytes())
            saved = {obj.path_id: obj for obj in check.objects}
            changed = {row["pathId"]: row for row in rows}
            assert saved.keys() == before.keys()
            for key, obj in saved.items():
                row = changed.get(key)
                assert digest(obj.get_raw_data()) == (row["newSha256"] if row else before[key]), "Unrelated Shader/Texture/Compute/Audio object changed"
                if row:
                    tree = obj.read_typetree()
                    assert tree == expected[key], "Persisted shader metadata differs"
                    export = output / "objects" / (path.name + "." + str(key) + ".shader")
                    export.write_bytes(obj.get_raw_data())
                    row["objectFile"] = str(export.relative_to(output))
            manifest["files"].append({"file": str(relative), "shaders": rows, "unchangedRawObjectsVerified": len(before) - len(rows)})
        finally:
            env.file.reader.dispose()
    manifest["changedShaders"] = sum(len(f["shaders"]) for f in manifest["files"])
    manifest["changedRecords"] = sum(len(s["changedRecords"]) for f in manifest["files"] for s in f["shaders"])
    manifest["validatedSamplerLayouts"] = len(validated)
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(json.dumps({k: manifest[k] for k in ("changedShaders", "changedRecords", "validatedSamplerLayouts")}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    prepare(parser.parse_args().output)
