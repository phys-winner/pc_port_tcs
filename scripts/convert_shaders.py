"""Add semantic GLES programs to Unity 2020 containers without changing other objects."""
from __future__ import annotations

import argparse
from collections import defaultdict
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import subprocess
import sys

import lz4.block
import UnityPy

from unity_shader_blob import encode_record, read_programs, semantic_metadata as base_semantic_metadata
from shader_tail import parse_record

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/hlslcc_tool"))
from convert_with_metadata import build_rdef

SEGMENT_LIMIT = 16 * 1024 * 1024
VS_TYPES, FS_TYPES = {15, 16}, {17, 18}
# Exact abstract feature bits from Unity 2020.3.30f1 UnityEditor.Rendering.ShaderRequirements.
REQUIREMENTS = {1: "base", 2: "interpolators10", 4: "interpolators32", 8: "MRT4", 16: "MRT8",
    32: "derivatives", 64: "sampleLOD", 128: "fragCoord", 512: "integers", 1024: "texture2DArray",
    2048: "instancing", 4096: "geometry", 8192: "cubeArray", 16384: "compute", 32768: "randomWrite",
    65536: "tessellationCompute", 131072: "tessellationShaders", 262144: "sparseTexelResident",
    524288: "framebufferFetch", 1048576: "MSAATextureSamples"}
UNSUPPORTED_REQUIREMENTS = 4 | 16 | 4096 | 8192 | 65536 | 131072 | 262144 | 524288
STAGES = ("progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing")
PARAM_FIELDS = ("m_VectorParams", "m_MatrixParams", "m_TextureParams", "m_BufferParams", "m_ConstantBuffers", "m_ConstantBufferBindings", "m_UAVParams", "m_Samplers")


def require(condition, message):
    if not condition:
        raise ValueError(message)


class ShaderConversionOpen(ValueError):
    def __init__(self, passes, open_entries):
        super().__init__("No supported shader variant converted")
        self.passes, self.open_entries = passes, open_entries


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def dump(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    temporary.replace(path)


def extract_dxbc(record):
    code = record["program_code"]
    at = code.find(b"DXBC")
    require(at >= 0 and at + 32 <= len(code), "No complete DXBC header")
    size = struct.unpack_from("<I", code, at + 24)[0]
    require(size >= 32 and at + size <= len(code), "DXBC outside Unity program code")
    return code[at:at + size]


def storage_declarations(code):
    from convert_compute_shaders import OP
    require(len(code) >= 32 and code[:4] == b"DXBC" and struct.unpack_from("<I", code, 24)[0] == len(code), "Incomplete storage DXBC container")
    count = struct.unpack_from("<I", code, 28)[0]
    require(32 + count * 4 <= len(code), "Storage DXBC directory outside container")
    shader = None
    for index in range(count):
        at = struct.unpack_from("<I", code, 32 + index * 4)[0]
        require(at + 8 <= len(code), "Storage shader chunk offset invalid")
        size = struct.unpack_from("<I", code, at + 4)[0]
        require(at + 8 + size <= len(code), "Storage shader chunk extent invalid")
        if code[at:at + 4] in (b"SHDR", b"SHEX"):
            require(shader is None and size % 4 == 0, "Ambiguous storage shader token stream")
            shader = struct.unpack_from(f"<{size // 4}I", code, at + 8)
    require(shader and len(shader) >= 2 and shader[0] & 0xffff == 0x50 and shader[0] >> 16 in (0, 1) and shader[1] == len(shader), "Storage resources require complete SM5.0 graphics bytecode")
    result, at = {}, 2
    while at < len(shader):
        token = shader[at]
        opcode = token & 0x7ff
        require(opcode != OP["OPCODE_CUSTOMDATA"] or at + 1 < len(shader), "Truncated custom-data instruction")
        size = shader[at + 1] if opcode == OP["OPCODE_CUSTOMDATA"] else token >> 24 & 127
        require(size and at + size <= len(shader), "Storage shader instruction extent invalid")
        if opcode in (OP["OPCODE_DCL_RESOURCE_STRUCTURED"], OP["OPCODE_DCL_RESOURCE_RAW"]):
            require(size == (4 if opcode == OP["OPCODE_DCL_RESOURCE_STRUCTURED"] else 3), "Unexpected storage declaration layout")
            operand = shader[at + 1]
            require(not token >> 31 and not operand >> 31 and (operand >> 22 & 7) == 0, "Indirect storage declaration unsupported")
            slot = shader[at + 2]
            stride = shader[at + size - 1] if opcode == OP["OPCODE_DCL_RESOURCE_STRUCTURED"] else 4
            require(slot < 128 and slot not in result and 0 < stride <= 65536 and stride % 4 == 0, "Invalid storage register/stride")
            result[slot] = {"group": "texture", "kind": "structured" if opcode == OP["OPCODE_DCL_RESOURCE_STRUCTURED"] else "raw", "slot": slot, "stride": stride}
        at += size
    return result


def semantic_metadata(record, subprogram, common, names):
    storage = [item for item in record["tail"]["resources"] if item["type"] == 2]
    if not storage:
        return base_semantic_metadata(record, subprogram, common, names)
    require(not any(item["type"] == 3 for item in record["tail"]["resources"]), "Raster UAV needs an explicit producer/format contract")
    declarations = storage_declarations(extract_dxbc(record))
    require(set(declarations) == {item["index"] for item in storage}, "Storage declaration/semantic register map differs")
    altered_record, altered_common, altered_sp = copy.deepcopy(record), copy.deepcopy(common), copy.deepcopy(subprogram)
    altered_record["tail"]["resources"] = [item for item in record["tail"]["resources"] if item["type"] != 2]
    name_map = {index: name for name, index in (names.items() if isinstance(names, dict) else names)}
    for block in (altered_common, altered_sp.get("m_Parameters", {})):
        require(not block.get("m_UAVParams"), "Serialized raster UAV unsupported")
        for binding in block.get("m_BufferParams", []):
            name, index = name_map[binding["m_NameIndex"]], binding["m_Index"]
            require(any(item["name"] == name and item["index"] == index for item in storage), "Serialized storage binding absent from shader tail")
        block["m_BufferParams"] = []
    metadata = base_semantic_metadata(altered_record, altered_sp, altered_common, names)
    metadata["storageResources"] = [{**declarations[item["index"]], "name": item["name"]} for item in storage]
    metadata["recoveredBindings"]["resources"].extend(copy.deepcopy(storage))
    metadata["rawBindings"] = record["tail"]
    return metadata


def semantic_rdef(metadata, major):
    if not metadata.get("storageResources"):
        return build_rdef(metadata, major)
    from convert_compute_shaders import rdef_for
    require(major == 5, "Storage RDEF requires SM5")
    dimensions = {"texture1d": 2, "texture1darray": 3, "texture2d": 4, "texture2darray": 5, "texture2dms": 6,
                  "texture2dmsarray": 7, "texture3d": 8, "texturecube": 9, "texturecubearray": 10}
    resources = copy.deepcopy(metadata["storageResources"])
    for item in metadata["resources"]:
        require(item["kind"] in ("texture", "sampler"), "Unmapped storage RDEF resource kind")
        resources.append({"name": item["name"], "group": "texture" if item["kind"] == "texture" else "sampler",
            "kind": "typed" if item["kind"] == "texture" else "sampler", "slot": item["binding"],
            "dimension": dimensions[item["dimension"]] if item["kind"] == "texture" else 0, "return_type": 5 if item["kind"] == "texture" else 0})
    require(len({(item["group"], item["slot"]) for item in resources}) == len(resources), "Overlapping storage/texture bindings")
    return rdef_for({"constantBuffers": metadata["constantBuffers"], "resources": resources}, encode_precision=False)


def keyword_set(subprogram, record):
    # Record strings are authoritative names; serialized indices are retained independently.
    return frozenset(record["keywords"]), frozenset(record["local_keywords"])


def pair_variants(vertices, fragments):
    shared_global = set().union(*(v["keywords"][0] for v in vertices)) & set().union(*(v["keywords"][0] for v in fragments))
    shared_local = set().union(*(v["keywords"][1] for v in vertices)) & set().union(*(v["keywords"][1] for v in fragments))
    indexed = defaultdict(list)
    for fragment in fragments:
        global_words, local_words = fragment["keywords"]
        indexed[(fragment["tier"], global_words & shared_global, local_words & shared_local)].append(fragment)
    for vertex in vertices:
        global_words, local_words = vertex["keywords"]
        for fragment in indexed.get((vertex["tier"], global_words & shared_global, local_words & shared_local), []):
            yield vertex, fragment


def merge_parameters(common, particular):
    """Materialize shared desktop parameters before adding a backend with different layout."""
    result = copy.deepcopy(particular)
    for key in PARAM_FIELDS:
        merged = {item.get("m_NameIndex", (item.get("bindPoint"), item.get("sampler"))): copy.deepcopy(item) for item in common.get(key, [])}
        for item in particular.get(key, []):
            identity = item.get("m_NameIndex", (item.get("bindPoint"), item.get("sampler")))
            if key == "m_ConstantBuffers" and identity in merged:
                buffer = copy.deepcopy(item)
                if buffer["m_Size"] != merged[identity]["m_Size"]:
                    require(buffer.get("m_IsPartialCB") or merged[identity].get("m_IsPartialCB"), "Full shared cbuffer extent mismatch")
                    buffer["m_Size"] = max(buffer["m_Size"], merged[identity]["m_Size"])
                for field in ("m_MatrixParams", "m_VectorParams", "m_StructParams"):
                    members = {member["m_NameIndex"]: copy.deepcopy(member) for member in merged[identity].get(field, [])}
                    members.update({member["m_NameIndex"]: copy.deepcopy(member) for member in item.get(field, [])})
                    buffer[field] = list(members.values())
                merged[identity] = buffer
            else:
                merged[identity] = copy.deepcopy(item)
        result[key] = list(merged.values())
    return result


def normalize_samplers(source, reflection):
    by_texture = defaultdict(set)
    aliases = {}
    combined_names = set(reflection["textureSamplers"])
    texture_names = {item["name"].replace("[", "_").replace("]", "_") for item in reflection.get("resources", []) if item["type"] == 2}
    # Shadow declarations also contain an unused regular counterpart absent from textureSamplers.
    for name in re.findall(r"\b[A-Za-z_]\w*TEX_with_SMP\w+\b", source):
        texture = name.rsplit("TEX_with_SMP", 1)[0]
        if texture in texture_names or texture.removeprefix("hlslcc_zcmp") in texture_names:
            combined_names.add(name)
    for combined in combined_names:
        require("TEX_with_SMP" in combined, "Unknown HLSLcc sampler naming")
        texture, sampler = combined.rsplit("TEX_with_SMP", 1)
        by_texture[texture].add(sampler)
        aliases[combined] = texture
    require(all(len(samplers) == 1 for samplers in by_texture.values()), "One texture uses multiple sampler states")
    # HLSLcc emits an unused uncombined sampler declaration as well; remove exact duplicates after renaming.
    for old, new in sorted(aliases.items(), key=lambda item: -len(item[0])):
        source = re.sub(r"\b" + re.escape(old) + r"\b", new, source)
    declarations = {}
    output = []
    for line in source.splitlines():
        match = re.match(r"\s*uniform\s+(?:(?:lowp|mediump|highp)\s+)?([iu]?sampler\w+)\s+(\w+)\s*;", line)
        if match:
            typ, name = match.groups()
            require(name not in declarations or declarations[name] == typ, f"Inconsistent sampler types for {name}")
            if name in declarations:
                continue
            declarations[name] = typ
        output.append(line)
    return "\n".join(output) + "\n", aliases


def varying_declarations(source, direction):
    result = {}
    pattern = re.compile(r"^(?P<prefix>\s*(?:(?:flat|centroid|sample|noperspective)\s+)*" + direction + r"\s+(?:(?:highp|mediump|lowp)\s+)?)(?P<type>[biu]?vec[234]|float|int|uint|bool)\s+(?P<name>vs_\w+)\s*;", re.M)
    for match in pattern.finditer(source):
        result[match["name"]] = (match["type"], match)
    return result


def normalize_shadow_lod(source):
    shadows = re.findall(r"uniform\s+(?:(?:lowp|mediump|highp)\s+)?(samplerCubeShadow|sampler2DArrayShadow)\s+(\w+)\s*;", source)
    for typ, name in shadows:
        gradient = "vec3(0.0)" if typ == "samplerCubeShadow" else "vec2(0.0)"
        pattern = r"\btextureLod\(\s*" + re.escape(name) + r"\s*,\s*(\w+)\s*,\s*0(?:\.0+)?\s*\)"
        # GLES lacks these zero-LOD shadow overloads; zero gradients select the base mip.
        source = re.sub(pattern, lambda match: f"textureGrad({name}, {match[1]}, {gradient}, {gradient})", source)
    return source


def combine_stages(vertex, fragment):
    vertex = re.sub(r"layout\s*\(\s*location\s*=\s*\d+\s*\)\s*(?=(?:flat\s+)?out\b)", "", vertex)
    fragment = re.sub(r"layout\s*\(\s*location\s*=\s*\d+\s*\)\s*(?=(?:flat\s+)?in\b)", "", fragment)
    outputs, inputs = varying_declarations(vertex, "out"), varying_declarations(fragment, "in")
    for name, (typ, _) in inputs.items():
        require(name in outputs, f"Fragment varying has no vertex producer: {name}")
        require(outputs[name][0] == typ, f"Varying type mismatch: {name}: {outputs[name][0]} vs {typ}")
    return "#ifdef VERTEX\n" + vertex + "\n#endif\n#ifdef FRAGMENT\n" + fragment + "\n#endif\n"


def correct_depth_contract(shader_name, subshader, shader_pass, fragment):
    corrections = []
    if shader_name == "Hidden/PostProcessing/ScreenSpaceReflections":
        require(subshader == 0 and shader_pass in range(4), "SSR pass layout changed")
        if shader_pass in (0, 3):
            raw = "u_xlat10_27" if shader_pass == 0 else "u_xlat10_6"
            target = "u_xlat1" if shader_pass == 0 else "u_xlat3"
            old = f"{target} = hlslcc_mtx4x4_InverseProjectionMatrix[2] * vec4({raw}) + {target};"
            require(fragment.count(old) == 1 and
                    len(re.findall(r"\b" + re.escape(raw) + r" = textureLod\(_CameraDepthTexture, vs_TEXCOORD0\.xy, 0\.0\)\.x;", fragment)) == 1 and
                    fragment.count("uniform \tvec4 hlslcc_mtx4x4_InverseProjectionMatrix[4];") == 1,
                    "SSR raw-depth inverse-GL-projection source shape changed")
            fragment = fragment.replace(old, old.replace(f"vec4({raw})", f"vec4({raw} * 2.0 - 1.0)"))
            corrections.append("SSR raw depth 0..1 to GL inverse-projection NDC -1..1")
        else:
            require("_InverseProjectionMatrix" not in fragment, "Unexpected SSR inverse projection in unchanged pass")
    elif shader_name == "Hidden/PostProcessing/TemporalAntialiasing":
        require(subshader == 0 and shader_pass in (0, 1), "TAA pass layout changed")
        if shader_pass == 0:
            comparisons = (
                "u_xlatb21 = u_xlat0.z>=u_xlat1.z;",
                "u_xlatb21 = u_xlat10_3.x>=u_xlat0.z;",
                "u_xlatb21 = u_xlat2.z>=u_xlat0.z;",
                "u_xlatb14 = u_xlat10_1.x>=u_xlat0.z;",
            )
            require(fragment.count("texture(_CameraDepthTexture,") == 5 and
                    fragment.count("texture(_CameraMotionVectorsTexture, u_xlat0.xy)") == 1 and
                    all(fragment.count(line) == 1 for line in comparisons),
                    "TAA closest-fragment depth source shape changed")
            for old in comparisons:
                fragment = fragment.replace(old, old.replace(">=", "<="))
            corrections.append("TAA perspective closest fragment uses minimum normal-Z depth (four comparisons)")
        else:
            require("_CameraDepthTexture" not in fragment, "TAA orthographic pass unexpectedly dilates depth")
    return fragment, corrections


def correct_shadow_caster(pass_name, vertex):
    if pass_name.lower() != "shadowcaster" or "unity_LightShadowBias.y *" not in vertex:
        return vertex, []
    scalar = r"u_xlat\d+(?:\.[xyzw])?"
    near_pattern = re.compile(r"(?P<clamp>" + scalar + r") = min\((?P<clip>u_xlat\d+)\.w, (?P<depth>" + scalar + r")\);")
    near_matches = list(near_pattern.finditer(vertex))
    require(len(near_matches) == 1 and vertex.count("unity_LightShadowBias.y *") == 1 and
            re.search(r"\bvec4 unity_LightShadowBias;", vertex), "ShadowCaster CG near-clamp/uniform shape changed")
    near = near_matches[0]
    clamp_var, clip_var, depth_var = near["clamp"], near["clip"], near["depth"]
    lerp_pattern = re.compile(r"(?P<delta>" + scalar + r") = \(-" + re.escape(depth_var) + r"\) \+ " + re.escape(clamp_var) + r";\s*"
        + r"gl_Position\.z = unity_LightShadowBias\.y \* (?P=delta) \+ " + re.escape(depth_var) + r";")
    require(len(lerp_pattern.findall(vertex[near.end():])) == 1, "ShadowCaster near-clamp output dataflow changed")
    divisions = list(re.finditer(r"(?P<bias>" + scalar + r") = unity_LightShadowBias\.x / (?P<clip>u_xlat\d+)\.w;", vertex))
    result = vertex
    if divisions:
        require(len(divisions) == 1 and divisions[0]["clip"] == clip_var, "ShadowCaster linear bias clip dataflow changed")
        division = divisions[0]
        bias_var = division["bias"]
        old = f"{bias_var} = min({bias_var}, 0.0);\n    {bias_var} = max({bias_var}, -1.0);"
        require(vertex.count(old) == 1 and vertex.index(old) > division.end() and vertex.index(old) < near.start(), "ShadowCaster reversed linear-bias clamp shape changed")
        between = vertex[vertex.index(old) + len(old):near.start()]
        additions = (f"{depth_var} = {clip_var}.z + {bias_var};", f"{depth_var} = {bias_var} + {clip_var}.z;")
        require(sum(between.count(line) for line in set(additions)) == 1, "ShadowCaster biased-depth dataflow changed")
        result = result.replace(old, f"{bias_var} = max({bias_var}, 0.0);\n    {bias_var} = min({bias_var}, 1.0);")
    else:
        require("unity_LightShadowBias.x /" not in vertex and depth_var == clip_var + ".z", "ShadowCaster depth-only near clamp shape changed")
    result = result.replace(near.group(0), f"{clamp_var} = max(-{clip_var}.w, {depth_var});")
    return result, ["UnityCG UnityApplyLinearShadowBias GLES clamp and near plane" if divisions else "UnityCG UnityApplyLinearShadowBias GLES near plane (depth cube)"]


def correct_point_shadow_receiver(keywords, fragment):
    sampler = "hlslcc_zcmp_ShadowMapTexture"
    if "samplerCubeShadow " + sampler not in fragment:
        return fragment, []
    require("SHADOWS_CUBE" in keywords and bool({"POINT", "POINT_COOKIE"} & set(keywords)), "Cube-shadow receiver keyword contract changed")
    require(re.search(r"\bvec4 _LightProjectionParams;", fragment), "Cube-shadow projection uniform missing")
    scalar = r"u_xlat\d+(?:\.[xyzw])?"
    chain = re.compile(r"^(?P<indent>[ \t]+)(?P<depth>" + scalar + r") = (?P=depth) \* _LightProjectionParams\.w;\n"
        r"(?P=indent)(?P=depth) = _LightProjectionParams\.y / (?P=depth);\n"
        r"(?P=indent)(?P=depth) = (?P=depth) \+ \(-_LightProjectionParams\.x\);\n"
        r"(?P=indent)(?P=depth) = \(-(?P=depth)\) \+ 1\.0;\n", re.M)
    matches = list(chain.finditer(fragment))
    require(len(matches) == 1, "Cube-shadow native projection/reversed-depth source shape changed")
    match = matches[0]
    depth = match["depth"]
    calls = re.findall(r"\b(?:texture|textureGrad|textureLod)\(" + sampler + r",\s*(txVec\d+)[,)]", fragment)
    require(len(calls) in (1, 4), "Cube-shadow comparison tap count changed")
    for coordinate in calls:
        declaration = r"\bvec4 " + coordinate + r" = vec4\(u_xlat\d+\.xyz,\s*" + re.escape(depth) + r"\);"
        found = list(re.finditer(declaration, fragment))
        require(len(found) == 1 and found[0].start() > match.end(), "Cube-shadow compare reference depth dataflow changed")
    inverse = match["indent"] + depth + " = (-" + depth + ") + 1.0;\n"
    require(match.group(0).endswith(inverse), "Cube-shadow inverse line preimage changed")
    result = fragment[:match.start()] + match.group(0)[:-len(inverse)] + fragment[match.end():]
    return result, ["GLES native point depth-cube comparison uses normal projected depth; D3D reversal removed"]


def unify_uniform_blocks(vertex, fragment):
    if not (vertex.get("requiresUniformBufferObjects") or fragment.get("requiresUniformBufferObjects")):
        return vertex, fragment
    vertex, fragment = copy.deepcopy(vertex), copy.deepcopy(fragment)

    def merge_variables(first, second):
        items = {item["name"]: copy.deepcopy(item) for item in first}
        for item in second:
            if item["name"] not in items:
                items[item["name"]] = copy.deepcopy(item)
                continue
            previous = items[item["name"]]
            for field in ("offset", "class", "type", "rows", "columns", "elements", "structSize"):
                require(previous.get(field) == item.get(field), "UBO stage layout mismatch: " + item["name"])
            if item["class"] == "struct":
                previous["members"] = merge_variables(previous["members"], item["members"])
            previous["size"] = max(previous["size"], item["size"])
        return sorted(items.values(), key=lambda item: (item["offset"], item["name"]))

    buffers = {}
    for metadata in (vertex, fragment):
        for buffer in metadata["constantBuffers"]:
            if buffer["name"].startswith("$"):
                require(all(item["class"] != "struct" for item in buffer["variables"]), "Structured Globals require a separate native UBO layout")
                continue
            if buffer["name"] not in buffers:
                buffers[buffer["name"]] = copy.deepcopy(buffer)
            else:
                previous = buffers[buffer["name"]]
                previous["variables"] = merge_variables(previous["variables"], buffer["variables"])
                previous["size"] = max(previous["size"], buffer["size"])
    for metadata in (vertex, fragment):
        metadata["requiresUniformBufferObjects"] = True
        for buffer in metadata["constantBuffers"]:
            if not buffer["name"].startswith("$"):
                shared = buffers[buffer["name"]]
                buffer["variables"], buffer["size"] = copy.deepcopy(shared["variables"]), shared["size"]
    return vertex, fragment


def merged_tail(vertex, fragment, vertex_meta, fragment_meta):
    parameters = {}
    types = {"float": 0, "int": 1, "bool": 2, "half": 3, "uint": 5}
    cursor = 0
    blocks = {}
    for metadata in (vertex_meta, fragment_meta):
        for buffer in metadata["constantBuffers"]:
            if metadata.get("requiresUniformBufferObjects") and not buffer["name"].startswith("$"):
                blocks.setdefault(buffer["name"], copy.deepcopy(buffer))
                continue
            for variable in buffer["variables"]:
                item = {"name": variable["name"], "type": types[variable["type"]], "rows": variable["rows"],
                    "columns": variable["columns"], "matrix": int(variable["class"].startswith("matrix_")),
                    "array_size": variable.get("elements", 0)}
                if item["name"] in parameters:
                    require(all(parameters[item["name"]][key] == value for key, value in item.items()), "Stage uniform shape mismatch: " + item["name"])
                    continue
                cursor = (cursor + 15) & ~15
                item["byte_offset"] = cursor
                cursor += ((variable["size"] + 15) & ~15)
                parameters[item["name"]] = item
    resources = {}
    for metadata in (vertex_meta, fragment_meta):
        for item in metadata["recoveredBindings"]["resources"]:
            if item["type"] == 1:  # Flat uniforms have no native UBO binding.
                continue
            require(item["type"] in (0, 2, 4), "Unsupported native GLES resource kind")
            key = (item["type"], item["name"])
            if key in resources:
                for field in ("dimension", "multisampled"):
                    require(resources[key].get(field) == item.get(field), "Stage resource mismatch")
                continue
            resources[key] = copy.deepcopy(item)
    texture_index = 0
    for item in resources.values():
        if item["type"] == 0:
            item["index"] = texture_index
            texture_index += 1
    def parameter(variable):
        require(variable["class"] != "struct", "Nested UBO struct needs native tail representation")
        return {"name": variable["name"], "type": types[variable["type"]], "rows": variable["rows"], "columns": variable["columns"],
            "matrix": int(variable["class"].startswith("matrix_")), "array_size": variable.get("elements", 0), "byte_offset": variable["offset"]}
    block_groups = []
    for slot, buffer in enumerate(blocks.values()):
        group = {"name": buffer["name"], "used_size": buffer["size"], "parameters": [], "structures": []}
        for variable in buffer["variables"]:
            if variable["class"] == "struct":
                group["structures"].append({"name": variable["name"], "byte_offset": variable["offset"], "array_size": variable["elements"],
                    "struct_size": variable["structSize"], "parameters": [parameter(item) for item in variable["members"]]})
            else:
                group["parameters"].append(parameter(variable))
        block_groups.append(group)
        resources[(1, buffer["name"])] = {"name": buffer["name"], "type": 1, "index": slot, "extra": 0}
    return {"source_map": vertex["tail"]["source_map"], "channels": copy.deepcopy(vertex["tail"]["channels"]),
        "groups": [{"name": "", "used_size": 0, "parameters": [], "structures": []},
                   {"name": "$Globals", "used_size": cursor, "parameters": list(parameters.values()), "structures": []}] + block_groups,
        "resources": list(resources.values())}


def encode_segments(records, limit=SEGMENT_LIMIT):
    require(records, "Cannot encode an empty shader program directory")
    header_size = 4 + len(records) * 12
    require(header_size < limit, "Shader directory exceeds segment limit")
    segments = [bytearray(header_size)]
    struct.pack_into("<I", segments[0], 0, len(records))
    for index, record in enumerate(records):
        require(len(record) <= limit, "Individual shader record exceeds segment limit")
        if len(segments[-1]) + len(record) > limit:
            segments.append(bytearray())
        segment = len(segments) - 1
        offset = len(segments[-1])
        struct.pack_into("<III", segments[0], 4 + index * 12, offset, len(record), segment)
        segments[-1].extend(record)
    return [bytes(segment) for segment in segments]


class Converter:
    def __init__(self, output, tool, validator, read_caches=()):
        self.output, self.tool, self.validator = output, tool, validator
        self.read_caches = tuple(read_caches)
        self.tool_hash = sha(tool)
        self.ubo_tool = tool.with_name(tool.stem + "_ubo" + tool.suffix)
        self.ubo_tool_hash = sha(self.ubo_tool) if self.ubo_tool.is_file() else None
        self.stages = 0
        self.translated = {}
        self.corrected_stages = set()

    def validate_corrected_stage(self, source, stage):
        if not self.validator:
            return
        folder = self.output / "depth_corrections"
        folder.mkdir(exist_ok=True)
        key = stage + "_" + digest(source.encode("utf-8"))
        path = folder / (key + (".frag" if stage == "fragment" else ".vert"))
        if key in self.corrected_stages:
            return
        path.write_text(source, encoding="utf-8")
        result = subprocess.run([str(self.validator), "--target-env=opengl", "-fauto-map-locations", "-fauto-bind-uniforms",
            "-fshader-stage=" + stage, "-c", str(path), "-o", str(folder / (key + ".spv"))], capture_output=True, text=True)
        (folder / (key + ".log")).write_text(result.stdout + result.stderr, encoding="utf-8")
        require(result.returncode == 0, "Corrected " + stage + " validation failed: " + result.stderr.strip())
        self.corrected_stages.add(key)

    def stage(self, record, subprogram, common, names, stage, metadata=None):
        if metadata is None:
            metadata = semantic_metadata(record, subprogram, common, names)
        rdef_metadata = copy.deepcopy(metadata)
        for buffer in rdef_metadata["constantBuffers"]:
            buffer["size"] = (buffer["size"] + 15) & ~15
        rdef = semantic_rdef(rdef_metadata, 5 if record["program_type"] in (16, 18) else 4)
        code = extract_dxbc(record)
        ubo = bool(metadata.get("requiresUniformBufferObjects"))
        tool = self.ubo_tool if ubo else self.tool
        tool_hash = self.ubo_tool_hash if ubo else self.tool_hash
        require(tool_hash, "Required local UBO converter missing")
        key = digest(bytes.fromhex(tool_hash) + (b"UBO\0" if ubo else b"") + code + rdef)
        if key in self.translated:
            source, aliases = self.translated[key]
            return source, metadata, key, aliases
        folder = self.output / "cache" / key
        folder.mkdir(parents=True, exist_ok=True)
        glsl, reflection_path = folder / "stage.glsl", folder / "reflection.json"
        failure = folder / "native-failure.json"
        if failure.exists():
            raise ValueError(json.loads(failure.read_text(encoding="utf-8"))["reason"])
        if not glsl.exists() or not reflection_path.exists():
            for cache in self.read_caches:
                cached = cache / key
                if (cached / "stage.glsl").is_file() and (cached / "reflection.json").is_file():
                    glsl.write_bytes((cached / "stage.glsl").read_bytes())
                    reflection_path.write_bytes((cached / "reflection.json").read_bytes())
                    break
        if not glsl.exists() or not reflection_path.exists():
            (folder / "source.dxbc").write_bytes(code)
            (folder / "semantic.rdef").write_bytes(rdef)
            dump(folder / "semantic.json", metadata)
            command = [str(tool), str(folder / "source.dxbc"), str(glsl), str(reflection_path), str(folder / "semantic.rdef")]
            if ubo:
                command.append("--ubo")
            result = subprocess.run(command, capture_output=True, text=True)
            (folder / "converter.log").write_text(result.stdout + result.stderr, encoding="utf-8")
            if result.returncode != 0:
                reason = f"HLSLcc failed ({result.returncode}), cache {key}: {result.stderr.strip()}"
                dump(failure, {"reason": reason})
                raise ValueError(reason)
            self.stages += 1
        reflection = json.loads(reflection_path.read_text(encoding="utf-8"))
        source, aliases = normalize_samplers(glsl.read_text(encoding="utf-8"), reflection)
        source = normalize_shadow_lod(source)
        if metadata.get("storageResources") and "#extension GL_EXT_texture_buffer : require" in source:
            require(not re.search(r"\b(?:[iu]?samplerBuffer|[iu]?imageBuffer)\b", source), "Unreflected buffer texture")
            source = source.replace("#extension GL_EXT_texture_buffer : require\n", "")
            metadata["removedUnusedTextureBufferExtension"] = True
        require("UnknownResource" not in source and "TEX_with_SMP" not in source, "Unresolved GLSL resource")
        if self.validator:
            checked = folder / "normalized.glsl"
            checked.write_text(source, encoding="utf-8")
            result = subprocess.run([str(self.validator), "--target-env=opengl", "-fauto-map-locations", "-fauto-bind-uniforms", "-fshader-stage=" + stage, "-c", str(checked), "-o", str(folder / "normalized.spv")], capture_output=True, text=True)
            require(result.returncode == 0, "GLSL validation failed: " + result.stderr.strip())
            if metadata.get("storageResources"):
                from convert_compute_shaders import validate_spirv_layout
                validate_spirv_layout(folder / "normalized.spv", {"constantBuffers": [], "resources": metadata["storageResources"]})
        self.translated[key] = (source, aliases)
        return source, metadata, key, aliases

    def shader(self, tree, limit):
        records = read_programs(tree, 4)
        require(records, "No D3D11 program blob")
        require(9 not in tree["platforms"], "Shader already has GLES programs; preserve existing backend")
        output_records, record_indices, passes, open_entries = [], {}, [], []
        original_props = copy.deepcopy(tree["m_ParsedForm"]["m_PropInfo"])
        original_dependencies = copy.deepcopy(tree["m_Dependencies"])
        state_hashes = []
        for si, subshader in enumerate(tree["m_ParsedForm"]["m_SubShaders"]):
            for pi, shader_pass in enumerate(subshader["m_Passes"]):
                state_hashes.append(copy.deepcopy(shader_pass["m_State"]))
                label = {"subshader": si, "pass": pi, "name": shader_pass["m_State"]["m_Name"]}
                source_programs = {name: shader_pass[name] for name in STAGES}
                unsupported_stages = [name for name in STAGES[2:] if source_programs[name]["m_SubPrograms"]]
                if unsupported_stages:
                    open_entries.append({**label, "reason": "unsupported_stages", "stages": unsupported_stages})
                    continue
                stages = []
                for name, accepted in (("progVertex", VS_TYPES), ("progFragment", FS_TYPES)):
                    variants = []
                    for subprogram in source_programs[name]["m_SubPrograms"]:
                        if subprogram["m_GpuProgramType"] not in accepted:
                            continue
                        index = subprogram["m_BlobIndex"]
                        require(0 <= index < len(records), "Subprogram blob index outside directory")
                        record = records[index]
                        require(record["program_type"] in accepted, "Serialized/blob shader stage mismatch")
                        variants.append({"subprogram": subprogram, "record": record, "tier": subprogram["m_ShaderHardwareTier"], "keywords": keyword_set(subprogram, record)})
                    stages.append(variants)
                if not all(stages):
                    if shader_pass["m_Type"] == 0:
                        open_entries.append({**label, "reason": "missing_vertex_or_fragment"})
                    continue
                additions, attempted, successes, covered = [], 0, 0, set()
                depth_corrections = defaultdict(int)
                truncated = False
                for vertex, fragment in pair_variants(*stages):
                    attempted += 1
                    if limit and successes >= limit:
                        open_entries.append({**label, "reason": "diagnostic_variant_limit", "limit": limit})
                        truncated = True
                        break
                    requirements = vertex["subprogram"]["m_ShaderRequirements"] | fragment["subprogram"]["m_ShaderRequirements"]
                    unsupported = requirements & UNSUPPORTED_REQUIREMENTS
                    unknown = requirements & ~sum(REQUIREMENTS)
                    if unsupported or unknown:
                        open_entries.append({**label, "reason": "requirements_not_supported_by_profile", "bits": unsupported | unknown, "features": [name for bit, name in REQUIREMENTS.items() if (unsupported | unknown) & bit]})
                        continue
                    try:
                        vm = semantic_metadata(vertex["record"], vertex["subprogram"], shader_pass["progVertex"]["m_CommonParameters"], shader_pass["m_NameIndices"])
                        fm = semantic_metadata(fragment["record"], fragment["subprogram"], shader_pass["progFragment"]["m_CommonParameters"], shader_pass["m_NameIndices"])
                        vm, fm = unify_uniform_blocks(vm, fm)
                        vs, vm, vk, va = self.stage(vertex["record"], vertex["subprogram"], shader_pass["progVertex"]["m_CommonParameters"], shader_pass["m_NameIndices"], "vertex", vm)
                        fs, fm, fk, fa = self.stage(fragment["record"], fragment["subprogram"], shader_pass["progFragment"]["m_CommonParameters"], shader_pass["m_NameIndices"], "fragment", fm)
                        vs, shadow_corrections = correct_shadow_caster(shader_pass["m_State"]["m_Name"], vs)
                        if shadow_corrections:
                            self.validate_corrected_stage(vs, "vertex")
                            for correction in shadow_corrections:
                                depth_corrections[correction] += 1
                        fs, corrections = correct_depth_contract(tree["m_ParsedForm"]["m_Name"], si, pi, fs)
                        fs, point_corrections = correct_point_shadow_receiver(vertex["keywords"][0] | fragment["keywords"][0], fs)
                        corrections += point_corrections
                        if corrections:
                            self.validate_corrected_stage(fs, "fragment")
                            for correction in corrections:
                                depth_corrections[correction] += 1
                        combined = combine_stages(vs, fs)
                        tail = merged_tail(vertex["record"], fragment["record"], vm, fm)
                        keywords = sorted(vertex["keywords"][0] | fragment["keywords"][0])
                        local_keywords = sorted(vertex["keywords"][1] | fragment["keywords"][1])
                        record = {"version": 201806140, "program_type": 4, "stats": [0, 0, 0, 0],
                            "keywords": keywords, "local_keywords": local_keywords, "program_code": combined.encode("utf-8"), "tail": tail}
                        encoded = encode_record(record)
                        require(encode_record(parse_record(encoded)) == encoded, "GLES program record round-trip changed bytes")
                        identity = digest(encoded)
                        if identity not in record_indices:
                            record_indices[identity] = len(output_records)
                            output_records.append(encoded)
                            (self.output / "programs").mkdir(exist_ok=True)
                            (self.output / "programs" / (identity + ".glsl")).write_text(combined, encoding="utf-8")
                        merged = copy.deepcopy(vertex["subprogram"])
                        merged.update(m_BlobIndex=record_indices[identity], m_GpuProgramType=4,
                            m_GlobalKeywordIndices=sorted(set(vertex["subprogram"]["m_GlobalKeywordIndices"]) | set(fragment["subprogram"]["m_GlobalKeywordIndices"])),
                            m_LocalKeywordIndices=sorted(set(vertex["subprogram"]["m_LocalKeywordIndices"]) | set(fragment["subprogram"]["m_LocalKeywordIndices"])),
                            m_Parameters={key: [] for key in PARAM_FIELDS}, m_ShaderRequirements=requirements)
                        for tier in (range(3) if vertex["tier"] == 3 else (vertex["tier"],)):
                            additions.append({**copy.deepcopy(merged), "m_ShaderHardwareTier": tier})
                        covered.update((id(vertex), id(fragment)))
                        successes += 1
                        if successes % 200 == 0:
                            print(f"Pass {si}:{pi} {label['name']}: {successes} pairs, {self.stages} native stages", flush=True)
                    except (ValueError, KeyError, OSError, struct.error) as error:
                        open_entries.append({**label, "reason": str(error), "vertex_blob": vertex["record"]["index"], "fragment_blob": fragment["record"]["index"], "keywords": sorted(vertex["keywords"][0] | fragment["keywords"][0])})
                if not truncated:
                    missing = [item for stage_variants in stages for item in stage_variants if id(item) not in covered]
                    if missing:
                        open_entries.append({**label, "reason": "source_variants_without_converted_pair", "count": len(missing), "examples": [{"blob": item["record"]["index"], "keywords": sorted(item["keywords"][0])} for item in missing[:10]]})
                if additions:
                    program = shader_pass["progVertex"]
                    common = program["m_CommonParameters"]
                    for subprogram in program["m_SubPrograms"]:
                        subprogram["m_Parameters"] = merge_parameters(common, subprogram["m_Parameters"])
                    program["m_CommonParameters"] = {key: [] for key in PARAM_FIELDS}
                    program["m_SubPrograms"].extend(additions)
                    shader_pass["m_Platforms"].append(9)
                    hash_template = copy.deepcopy(shader_pass["m_EditorDataHash"][0])
                    raw_hash = hashlib.md5(b"".join(output_records[index] for index in sorted({item["m_BlobIndex"] for item in additions}))).digest()
                    for index in range(16):
                        hash_template[f"bytes[{index}]"] = raw_hash[index]
                    shader_pass["m_EditorDataHash"].append(hash_template)
                passes.append({**label, "pairs_attempted": attempted, "pairs_converted": successes, "gles_subprograms": len(additions), "stage_variants": list(map(len, stages)), "stage_variants_covered": len(covered), "depth_corrections": dict(depth_corrections)})
        if not output_records:
            raise ShaderConversionOpen(passes, open_entries)
        segments = encode_segments(output_records)
        old_blob = bytes(tree["compressedBlob"])
        offsets, sizes, raw_sizes, appended = [], [], [], bytearray()
        for segment in segments:
            packed = lz4.block.compress(segment, store_size=False)
            offsets.append(len(old_blob) + len(appended)); sizes.append(len(packed)); raw_sizes.append(len(segment)); appended.extend(packed)
        tree["platforms"].append(9)
        tree["offsets"].append(offsets); tree["compressedLengths"].append(sizes); tree["decompressedLengths"].append(raw_sizes)
        tree["compressedBlob"] = old_blob + appended
        require([r["record_bytes"] for r in read_programs(tree, 9)] == output_records, "GLES directory/segments did not round-trip")
        require(tree["m_ParsedForm"]["m_PropInfo"] == original_props and tree["m_Dependencies"] == original_dependencies, "Shader properties or dependency PPtrs changed")
        require([p["m_State"] for s in tree["m_ParsedForm"]["m_SubShaders"] for p in s["m_Passes"]] == state_hashes, "Author render states changed")
        return {"records": len(output_records), "passes": passes, "open": open_entries,
            "coordinate_gate": "Runtime GL matrices retained; baked D3D reversed-Z/depth/UV behavior requires device comparison",
            "binding_gate": "Semantic uniforms, named UBOs when structures require them, and original vertex channels; native reflected uploads require device verification"}


def convert_file(path, args, converter):
    relative = path.relative_to(args.source)
    before_hash = sha(path)
    environment = UnityPy.load(str(path))
    objects = {obj.path_id: obj for obj in environment.objects}
    before = {key: (obj.type_id, digest(obj.get_raw_data())) for key, obj in objects.items()}
    externals = [(item.path, item.guid, item.type) for item in environment.file.externals]
    changed, reports = set(), []
    for key, obj in objects.items():
        if obj.type.name == "ComputeShader":
            reports.append({"path_id": key, "name": obj.read_typetree().get("m_Name", ""), "status": "OPEN", "reason": "ComputeShader container preserved; conversion not implemented"})
        if obj.type.name != "Shader":
            continue
        tree = obj.read_typetree()
        name = tree["m_ParsedForm"]["m_Name"]
        if args.shader and name not in args.shader:
            continue
        if 9 in tree["platforms"]:
            reports.append({"path_id": key, "name": name, "status": "EXISTING_GLES_PRESERVED"})
            continue
        try:
            result = converter.shader(tree, args.limit_per_pass)
            raw = obj.save_typetree(tree)
            changed.add(key)
            raw_path = args.output / "objects" / relative.parent / (path.name + "." + str(key) + ".shader")
            raw_path.parent.mkdir(parents=True, exist_ok=True)
            raw_path.write_bytes(raw)
            reports.append({"path_id": key, "name": name, "status": "CANDIDATE" if result["open"] else "CONVERTED_STATIC", **result,
                "source_object_sha256": before[key][1], "object_file": str(raw_path), "object_sha256": sha(raw_path)})
            print(f"Shader {path.name}:{key} {name}: {result['records']} GLES records, {len(result['open'])} open entries", flush=True)
        except (ValueError, KeyError, OSError, struct.error) as error:
            report = {"path_id": key, "name": name, "status": "OPEN", "reason": str(error)}
            if isinstance(error, ShaderConversionOpen):
                report.update(passes=error.passes, open=error.open_entries)
            reports.append(report)
            print(f"Shader {path.name}:{key} {name}: OPEN {error}", flush=True)
    output = None
    if changed:
        output = args.output / relative
        output.parent.mkdir(parents=True, exist_ok=True)
        temporary = output.with_suffix(output.suffix + ".tmp")
        temporary.write_bytes(environment.file.save())
        check = UnityPy.load(str(temporary))
        after = {obj.path_id: (obj.type_id, digest(obj.get_raw_data())) for obj in check.objects}
        require(before.keys() == after.keys(), "Object path IDs changed")
        require(all(value == after[key] for key, value in before.items() if key not in changed), "Unrelated object bytes or PPtrs changed")
        persisted = {obj.path_id: obj for obj in check.objects}
        for entry in reports:
            if entry.get("object_file"):
                key = entry["path_id"]
                require(after[key][1] == entry["object_sha256"], "Exported Shader bytes differ from saved container")
                saved_tree = persisted[key].read_typetree()
                require(9 in saved_tree["platforms"] and len(read_programs(saved_tree, 9)) == entry["records"], "Saved Shader GLES selection/records missing")
                entry["persistent_gles_verified"] = True
        require(externals == [(item.path, item.guid, item.type) for item in check.file.externals], "External file/PPtr mapping changed")
        check.file.reader.dispose()
        temporary.replace(output)
    environment.file.reader.dispose()
    require(sha(path) == before_hash, "Input changed during conversion")
    return {"source": str(path), "relative_path": relative.as_posix(), "source_sha256": before_hash, "output": str(output) if output else None,
        "output_sha256": sha(output) if output else None, "unchanged_objects_verified": len(before) - len(changed), "changed_path_ids": sorted(changed), "shaders": reports}


def refresh_exports(manifest_path):
    """Recover raw exports from already verified, persisted containers without retranslating."""
    manifest_path = manifest_path.resolve(strict=True)
    require((ROOT / "work").resolve() in manifest_path.parents, "Manifest must be under project work")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    exported = 0
    for file in manifest["files"]:
        if not file.get("output"):
            continue
        output = Path(file["output"]).resolve(strict=True)
        require((ROOT / "work").resolve() in output.parents, "Container must be under project work")
        require(sha(output) == file["output_sha256"], "Completed output container changed")
        environment = UnityPy.load(str(output))
        objects = {obj.path_id: obj for obj in environment.objects}
        for entry in file["shaders"]:
            if not entry.get("object_file"):
                continue
            obj = objects[entry["path_id"]]
            tree = obj.read_typetree()
            require(tree["m_ParsedForm"]["m_Name"] == entry["name"], "Completed Shader identity mismatch")
            require(9 in tree["platforms"] and len(read_programs(tree, 9)) == entry["records"], "Completed Shader missing GLES backend/record count")
            raw = obj.get_raw_data()  # This reader is from the newly saved container.
            raw_path = Path(entry["object_file"]).resolve()
            require((ROOT / "work").resolve() in raw_path.parents, "Raw export must be under project work")
            temporary = raw_path.with_name(raw_path.name + ".tmp")
            temporary.write_bytes(raw)
            temporary.replace(raw_path)
            entry["object_sha256"] = digest(raw)
            entry["persistent_gles_verified"] = True
            exported += 1
        environment.file.reader.dispose()
    manifest["raw_export_sha256"] = sha(__file__)
    manifest["raw_export_source"] = "Persisted output container; original-reader get_raw_data export bug repaired"
    dump(manifest_path, manifest)
    print(f"PASS: {manifest_path.parent.name}: {exported} persisted GLES objects exported and verified", flush=True)


def self_test():
    def variant(global_words, local_words=(), tier=3):
        return {"keywords": (frozenset(global_words), frozenset(local_words)), "tier": tier}
    vs = [variant(()), variant(("SHARED", "VS_ONLY"))]
    fs = [variant(()), variant(("SHARED", "FS_ONLY"))]
    pairs = list(pair_variants(vs, fs))
    assert len(pairs) == 2 and pairs[1][0] is vs[1] and pairs[1][1] is fs[1]
    assert not list(pair_variants([variant((), tier=0)], [variant((), tier=1)]))
    segment = encode_segments([b"abcd", b"efgh", b"ijkl"], limit=48)
    assert len(segment) == 2 and struct.unpack_from("<III", segment[0], 28) == (0, 4, 1)
    normalized, aliases = normalize_samplers("uniform sampler2D _MainTexTEX_with_SMPsampler0;\nuniform sampler2D _MainTex;\nvoid main(){texture(_MainTexTEX_with_SMPsampler0,vec2(0));}\n", {"textureSamplers": ["_MainTexTEX_with_SMPsampler0"]})
    assert "TEX_with_SMP" not in normalized and normalized.count("uniform sampler2D _MainTex;") == 1
    shadow, _ = normalize_samplers("uniform sampler2DShadow hlslcc_zcmp_ShadowTEX_with_SMPs0;\nuniform sampler2D _ShadowTEX_with_SMPs0;\n", {"textureSamplers": ["hlslcc_zcmp_ShadowTEX_with_SMPs0"], "resources": [{"name": "_Shadow", "type": 2}]})
    assert "TEX_with_SMP" not in shadow and "hlslcc_zcmp_Shadow" in shadow
    shadow_lod = normalize_shadow_lod("uniform samplerCubeShadow s; void main(){textureLod(s,p,0.0);textureLod(s,p,1.0);}")
    assert "textureGrad(s, p, vec3(0.0), vec3(0.0))" in shadow_lod and "textureLod(s,p,1.0)" in shadow_lod
    try:
        normalize_samplers("", {"textureSamplers": ["TEX_with_SMPs0", "TEX_with_SMPs1"]})
    except ValueError:
        pass
    else:
        raise AssertionError("Conflicting sampler states accepted")
    first = {"requiresUniformBufferObjects": True, "constantBuffers": [{"name": "UnityShared", "binding": 1, "size": 32,
        "variables": [{"name": "a", "offset": 0, "size": 16, "class": "vector", "type": "float", "rows": 1, "columns": 4}]}]}
    second = {"constantBuffers": [{"name": "UnityShared", "binding": 3, "size": 32,
        "variables": [{"name": "b", "offset": 16, "size": 16, "class": "vector", "type": "float", "rows": 1, "columns": 4}]}]}
    left, right = unify_uniform_blocks(first, second)
    assert left["constantBuffers"][0]["variables"] == right["constantBuffers"][0]["variables"]
    assert len(left["constantBuffers"][0]["variables"]) == 2 and right["requiresUniformBufferObjects"]
    assert len(first["constantBuffers"][0]["variables"]) == 1 and "requiresUniformBufferObjects" not in second
    conflicting = copy.deepcopy(first)
    conflicting["constantBuffers"][0]["variables"][0]["offset"] = 16
    try:
        unify_uniform_blocks(first, conflicting)
    except ValueError:
        pass
    else:
        raise AssertionError("Conflicting UBO stage layouts accepted")
    near, far = 0.1, 1000.0
    for eye in (near, 0.25, 1.0, 10.0, 100.0, far):
        ndc = (far + near) / (far - near) - (2 * far * near) / ((far - near) * eye)
        raw = 0.5 * (ndc + 1)
        restored = (2 * far * near) / ((far + near) - (2 * raw - 1) * (far - near))
        assert math.isclose(restored, eye, rel_tol=1e-10)
        linear_normal = 1 / ((1 - far / near) * raw + far / near)
        linear_reverse = 1 / ((far / near - 1) * (1 - raw) + 1)
        assert math.isclose(linear_normal, eye / far, rel_tol=1e-10)
        assert math.isclose(linear_reverse, eye / far, rel_tol=1e-10)
    normal_depths = (0.8, 0.75, 0.3, 0.9, 0.45)
    reverse_depths = [1 - value for value in normal_depths]
    nearest = min(range(5), key=lambda index: normal_depths[index])
    assert nearest == max(range(5), key=lambda index: reverse_depths[index]) == 2
    assert nearest != max(range(5), key=lambda index: normal_depths[index])
    fixtures = ROOT / "work/evidence/depth_uv_audit"
    if (fixtures / "823_0_0.glsl").is_file():
        original = (fixtures / "823_0_0.glsl").read_text(encoding="utf-8")
        corrected, markers = correct_depth_contract("Hidden/PostProcessing/TemporalAntialiasing", 0, 0, original)
        assert len(markers) == 1 and corrected != original
        for before, after in zip(original.splitlines(), corrected.splitlines()):
            assert before == after or (">=" in before and after == before.replace(">=", "<="))
        try:
            correct_depth_contract("Hidden/PostProcessing/TemporalAntialiasing", 0, 0, original.replace("u_xlat0.z>=u_xlat1.z", "u_xlat0.z>u_xlat1.z"))
        except ValueError:
            pass
        else:
            raise AssertionError("Changed TAA instruction shape accepted")
        for shader_pass in (0, 3):
            original = (fixtures / f"853_0_{shader_pass}.glsl").read_text(encoding="utf-8")
            corrected, markers = correct_depth_contract("Hidden/PostProcessing/ScreenSpaceReflections", 0, shader_pass, original)
            assert len(markers) == 1 and corrected.count(" * 2.0 - 1.0)") == 1
        for shader_pass in (1, 2):
            original = (fixtures / f"853_0_{shader_pass}.glsl").read_text(encoding="utf-8")
            assert correct_depth_contract("Hidden/PostProcessing/ScreenSpaceReflections", 0, shader_pass, original) == (original, [])
        original = (fixtures / "823_0_1.glsl").read_text(encoding="utf-8")
        assert correct_depth_contract("Hidden/PostProcessing/TemporalAntialiasing", 0, 1, original) == (original, [])
    def shadow_cg_gl(z, w, bias_x, bias_y):
        biased = z + max(0, min(bias_x / w, 1))
        return biased + bias_y * (max(biased, -w) - biased)
    assert math.isclose(shadow_cg_gl(-1.2, 1, 0.1, 1), -1)
    assert math.isclose(shadow_cg_gl(0.2, 1, 0.1, 0), 0.3)
    assert math.isclose(shadow_cg_gl(1.2, 1, 0, 1), 1.2)
    for z in (-2, -0.5, 0.25, 2):
        for w in (0.5, 1, 2):
            for bx in (-0.1, 0, 0.1):
                for by in (0, 0.5, 1):
                    biased = z + min(1, max(bx / w, 0))
                    assert math.isclose(shadow_cg_gl(z, w, bx, by), (1 - by) * biased + by * max(-w, biased), abs_tol=1e-12)
    shadow_fixture = fixtures / "798_0_2.glsl"
    if shadow_fixture.is_file():
        original = shadow_fixture.read_text(encoding="utf-8")
        corrected, markers = correct_shadow_caster("ShadowCaster", original)
        assert len(markers) == 1 and "u_xlat1.x = max(-u_xlat0.w, u_xlat6);" in corrected
        assert "u_xlat1.x = max(u_xlat1.x, 0.0);" in corrected and "u_xlat1.x = min(u_xlat1.x, 1.0);" in corrected
        assert correct_shadow_caster("FORWARD", original) == (original, [])
        try:
            correct_shadow_caster("ShadowCaster", original.replace("u_xlat6 = u_xlat0.z + u_xlat1.x;", "u_xlat6 = u_xlat0.x + u_xlat1.x;"))
        except ValueError:
            pass
        else:
            raise AssertionError("Changed shadow depth dataflow accepted")
    near, far = 0.2, 10.0 * 1.01
    projection_x, projection_y = far / (near - far), near * far / (near - far)
    def point_depth(axis):
        return -projection_x + projection_y / axis
    assert math.isclose(point_depth(near), 0, abs_tol=1e-12) and math.isclose(point_depth(far), 1)
    for axis in (near, 0.5, 1, 2, 3, far):
        assert math.isclose(projection_y / (point_depth(axis) + projection_x), axis, rel_tol=1e-12)
    occluder = point_depth(2)
    assert point_depth(1) <= occluder and not point_depth(3) <= occluder
    assert (1 - point_depth(3)) <= occluder  # The baked D3D branch wrongly lights this receiver.
    point_fixture = fixtures / "deferred-point-shadow.glsl"
    if point_fixture.is_file():
        original = point_fixture.read_text(encoding="utf-8").split("\n#endif\n#ifdef FRAGMENT\n", 1)[1][:-len("\n#endif\n")]
        corrected, markers = correct_point_shadow_receiver({"POINT", "SHADOWS_CUBE"}, original)
        assert len(markers) == 1 and len(original.splitlines()) - len(corrected.splitlines()) == 1
        assert "u_xlat13 = (-u_xlat13) + 1.0;" not in corrected
        assert correct_point_shadow_receiver(set(), original.replace("samplerCubeShadow", "samplerCube"))[0] == original.replace("samplerCubeShadow", "samplerCube")
        for damaged in (original.replace("_LightProjectionParams.y / u_xlat13", "_LightProjectionParams.x / u_xlat13"),
                        original.replace("vec4(u_xlat6.xyz,u_xlat13)", "vec4(u_xlat6.xyz,u_xlat0.x)")):
            assert damaged != original
            try:
                correct_point_shadow_receiver({"POINT", "SHADOWS_CUBE"}, damaged)
            except ValueError:
                pass
            else:
                raise AssertionError("Changed point-shadow reference dataflow accepted")
    print("PASS: pairing, segmented writer, semantic samplers, UBO layouts, analytic depth/shadow math and guarded SSR/TAA/ShadowCaster/point-receiver corrections")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=Path(r"external/game/TheClosingShift_Data"))
    parser.add_argument("--output", type=Path, default=ROOT / "work/android_shaders")
    parser.add_argument("--asset", action="append")
    parser.add_argument("--shader", action="append")
    parser.add_argument("--limit-per-pass", type=int, default=0, help="Explicit diagnostic truncation; leaves OPEN entries")
    parser.add_argument("--validate", action="store_true")
    parser.add_argument("--cache-read", type=Path, action="append", default=[], help="Read validated native-stage cache from an earlier batch; hash-keyed stages are copied into this output")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--refresh-exports", type=Path, action="append", default=[], help="Re-export completed raw Shader objects from their persisted output containers")
    args = parser.parse_args()
    if args.self_test:
        self_test(); return
    if args.refresh_exports:
        for manifest_path in args.refresh_exports:
            refresh_exports(manifest_path)
        return
    args.source, args.output = args.source.resolve(), args.output.resolve()
    require((ROOT / "work").resolve() in args.output.parents, "Output must be under project work directory")
    require(args.output != args.source and args.source not in args.output.parents, "Output must not be inside input directory")
    require(args.limit_per_pass >= 0, "Negative diagnostic variant limit")
    args.output.mkdir(parents=True, exist_ok=True)
    validator = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/ndk/27.2.12479018/shader-tools/windows-x86_64/glslc.exe' if args.validate else None
    tool = ROOT / "work/hlslcc_build/hlslcc_tool.exe"
    require(tool.is_file() and (validator is None or validator.is_file()), "Required local shader tool missing")
    converter = Converter(args.output, tool, validator, [path.resolve() for path in args.cache_read])
    files = [args.source / name for name in args.asset] if args.asset else sorted(args.source.glob("*.assets")) + [args.source / "Resources/unity_builtin_extra"]
    require(files, "No input asset containers")
    manifest = {"tool_sha256": converter.tool_hash, "ubo_tool_sha256": converter.ubo_tool_hash, "converter_sha256": sha(__file__),
        "semantic_reader_sha256": sha(ROOT / "scripts/unity_shader_blob.py"), "gles_platform": 9, "gpu_program_type": 4,
        "profile": "GLES 3.1 vertex/fragment; original abstract feature requirements retained",
        "runtime_accepted": False, "files": []}
    for path in files:
        path = path.resolve()
        require(args.source in path.parents, "Asset filename escapes source directory")
        manifest["files"].append(convert_file(path, args, converter))
        dump(args.output / "manifest.json", manifest)
    reasons = defaultdict(int)
    for file in manifest["files"]:
        for shader in file["shaders"]:
            if shader["status"] == "OPEN":
                reasons[shader["reason"]] += 1
            for entry in shader.get("open", []):
                reason = entry["reason"]
                if "cache " in reason:
                    reason = reason.split("cache ", 1)[0] + "(cache references in full manifest)"
                reasons[reason] += 1
    dump(args.output / "known_limits.json", {"runtime_accepted": False, "open_reasons": dict(reasons),
        "device_checks": ["linked native GLES draw/framebuffer orientation", "D3D baked reversed-Z/depth/UV behavior", "author effects and shadows", "actual instancing-enabled materials"],
        "source_preservation": "Unsupported records/backends remain unchanged; no replacement color shader or feature disabling"})
    print("PASS: output containers preserve object IDs and every unrelated raw object; device gates remain open", flush=True)


if __name__ == "__main__":
    main()
