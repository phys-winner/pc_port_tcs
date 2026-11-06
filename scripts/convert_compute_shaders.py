"""Recover compute reflection from Unity metadata and validate GLES candidates offline."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess

import UnityPy
from hlslcc_tool.convert_with_metadata import build_rdef
from repair_compute_sampler_bindings import parallel_sampler_bindings

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r"external/game/TheClosingShift_Data")
SOURCE_HASHES = {
    "resources.assets": "3a59651d81dcb8da420f32401abe60300eca182c238f3c67d988001c66156c84",
    "sharedassets0.assets": "f84718596074e3945a17c6ab436319dbad76071dbcdb0ee03903e263ed8655f6",
}
TOOL = ROOT / "work/compute_hlslcc_build/compute_hlslcc.exe"
VALIDATOR = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/ndk/27.2.12479018/shader-tools/windows-x86_64/glslc.exe'
BUILTIN_REFERENCE = SOURCE / "Resources/unity default resources"
BUILTIN_REFERENCE_HASH = "458713a9e0aca9b787f40e355055a9e8f8193d0a203058b21164035fc573ad4a"
TYPES = {0: "float", 1: "int", 2: "bool", 3: "half", 5: "uint"}
DIMENSION = {1: 2, 2: 4, 3: 8, 4: 9, 5: 5, 6: 10}
TOKEN_DIMENSION = {1: 1, 2: 2, 3: 4, 4: 6, 5: 8, 6: 9, 7: 3, 8: 5, 9: 7, 10: 10}
# These image formats come from Frustum.cs SetTexture -> TextureBuffers.cs allocation.
IMAGE_FORMATS = {
    ("ComputeAccumulationComputeShader", "fogVolumeTexture"): "rgba16f",
    ("ApplyBlurFilterComputeShader", "destinationTexture"): "rgba16f",
    ("ApplyDenoisingFilterComputeShader", "destinationTexture"): "rgba16f",
    ("ComputeDataComputeShader", "lightingTexture"): "rgba16f",
    ("RenderLightProbesTextureComputeShader", "lightProbesCoefficientsTexture"): "rgba16f",
    ("ComputeVisibleCellsBufferComputeShader", "maximumSliceAmountTexture"): "r32i",
    # Requires the corresponding RHalf -> RFloat producer patch in Aura2_Core.
    ("ComputeMaximumDepthComputeShader", "occlusionTexture"): "r32f",
}


class Unsupported(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise Unsupported(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def opcodes():
    header = ROOT / "work/tools/HLSLcc/src/internal_includes/tokens.h"
    body = header.read_text().split("enum OPCODE_TYPE", 1)[1].split("};", 1)[0]
    values, current = {}, -1
    for name, explicit in re.findall(r"^\s*(OPCODE_\w+)\s*(?:=\s*(-?(?:0x[\da-fA-F]+|\d+)))?\s*,", body, re.M):
        current = int(explicit, 0) if explicit else current + 1
        values[name] = current
    require(values["OPCODE_ADD"] == 0 and values["OPCODE_DCL_THREAD_GROUP"] == 155, "Unexpected authorized HLSLcc opcode table")
    return values


OP = opcodes()


def gles_target_reference():
    require(sha(BUILTIN_REFERENCE.read_bytes()) == BUILTIN_REFERENCE_HASH, "Original builtin compute reference changed")
    reference = UnityPy.load(str(BUILTIN_REFERENCE))
    # The 2020.3.30f1 game and installed Editor both bundle reusable builtin
    # resources whose own serialized version remains 2020.3.22f1.
    version = reference.file.unity_version
    require(version == "2020.3.22f1", "Bundled builtin compute reference version differs")
    evidence = []
    settings = set()
    for obj in reference.objects:
        if obj.type.name != "ComputeShader":
            continue
        tree = obj.read_typetree()
        for variant in tree["variants"]:
            if variant["targetRenderer"] == 11:
                values = (variant["targetRenderer"], variant["targetLevel"], variant["resourcesResolved"])
                settings.add(values)
                evidence.append({"path_id": obj.path_id, "name": tree["m_Name"], "targetRenderer": values[0], "targetLevel": values[1], "resourcesResolved": values[2]})
    require(settings == {(11, 3, True)} and len(evidence) >= 3, "Matching Unity builtin GLES compute target settings changed")
    reference.file.reader.dispose()
    return {"file": str(BUILTIN_REFERENCE), "sha256": BUILTIN_REFERENCE_HASH, "serialized_version": version, "game_player_version": "2020.3.30f1", "variants": evidence}


def declarations(code):
    require(len(code) >= 32 and code[:4] == b"DXBC", "Not DXBC")
    require(struct.unpack_from("<I", code, 24)[0] == len(code), "DXBC declared size differs")
    count = struct.unpack_from("<I", code, 28)[0]
    require(32 + count * 4 <= len(code), "Truncated DXBC directory")
    shader = None
    for index in range(count):
        offset = struct.unpack_from("<I", code, 32 + index * 4)[0]
        require(offset + 8 <= len(code), "DXBC chunk outside file")
        size = struct.unpack_from("<I", code, offset + 4)[0]
        require(offset + 8 + size <= len(code), "DXBC chunk extent outside file")
        if code[offset:offset + 4] in (b"SHDR", b"SHEX"):
            require(shader is None and size % 4 == 0, "Ambiguous/unaligned shader tokens")
            shader = struct.unpack_from(f"<{size // 4}I", code, offset + 8)
    require(shader is not None and shader[0] == 0x50050 and shader[1] == len(shader), "Only complete SM5.0 compute is supported")
    resources, buffers, threads, counters = {}, {}, None, {}
    at = 2
    names = {number: name for name, number in OP.items()}
    while at < len(shader):
        token = shader[at]
        name = names.get(token & 0x7ff, "")
        size = shader[at + 1] if name == "OPCODE_CUSTOMDATA" else (token >> 24) & 127
        require(size and at + size <= len(shader), "Invalid compute instruction extent")
        if name in {"OPCODE_IMM_ATOMIC_ALLOC", "OPCODE_IMM_ATOMIC_CONSUME"}:
            require(size == 5 and ((shader[at + 3] >> 12) & 255) == 30 and not shader[at + 3] >> 31,
                    "Unknown append/consume UAV operand encoding")
            slot = shader[at + 4]
            operation = "append" if name == "OPCODE_IMM_ATOMIC_ALLOC" else "consume"
            require(counters.get(slot, operation) == operation, "Mixed append and consume requires counter type11")
            counters[slot] = operation
        if name == "OPCODE_DCL_THREAD_GROUP":
            require(size == 4 and threads is None, "Malformed thread-group declaration")
            threads = list(shader[at + 1:at + 4])
        if name in {"OPCODE_DCL_RESOURCE", "OPCODE_DCL_SAMPLER", "OPCODE_DCL_CONSTANT_BUFFER", "OPCODE_DCL_RESOURCE_RAW", "OPCODE_DCL_RESOURCE_STRUCTURED", "OPCODE_DCL_UNORDERED_ACCESS_VIEW_TYPED", "OPCODE_DCL_UNORDERED_ACCESS_VIEW_RAW", "OPCODE_DCL_UNORDERED_ACCESS_VIEW_STRUCTURED"}:
            operand_at = at + 1
            require(not token >> 31, "Extended compute declaration opcode unsupported")
            operand = shader[operand_at]
            require(not operand >> 31 and ((operand >> 22) & 7) == 0, "Indirect/extended declaration index unsupported")
            slot = shader[operand_at + 1]
            require(slot < 128, "Compute resource slot exceeds parser bounds")
            if name == "OPCODE_DCL_CONSTANT_BUFFER":
                buffers[slot] = shader[operand_at + 2] * 16
            else:
                group = "sampler" if name == "OPCODE_DCL_SAMPLER" else "uav" if "UNORDERED_ACCESS" in name else "texture"
                kind = "raw" if name.endswith("_RAW") else "structured" if name.endswith("_STRUCTURED") else "sampler" if group == "sampler" else "typed"
                item = {"group": group, "slot": slot, "kind": kind}
                if kind == "typed":
                    dimension = (token >> 11) & 31
                    require(dimension in TOKEN_DIMENSION, "Unknown DXBC texture dimension")
                    item.update(dimension=TOKEN_DIMENSION[dimension], return_type=shader[at + size - 1] & 15)
                elif kind == "structured":
                    item["stride"] = shader[at + size - 1]
                    require(0 < item["stride"] <= 65536 and item["stride"] % 4 == 0, "Unaligned structured buffer stride")
                key = (group, slot)
                require(key not in resources, "Duplicate DXBC declaration")
                resources[key] = item
        at += size
    require(threads and all(0 < n <= 1024 for n in threads), "Invalid compute group dimensions")
    for slot, kind in counters.items():
        require(("uav", slot) in resources and resources[("uav", slot)]["kind"] == "structured", "Atomic counter does not refer to a structured UAV")
        resources[("uav", slot)]["counter"] = kind
    return {"resources": resources, "buffers": buffers, "threads": threads}


def semantic_buffer(buffer, slot):
    variables, occupied = [], set()
    for parameter in buffer["params"]:
        rows, columns, elements = parameter["rowCount"], parameter["colCount"], parameter["arraySize"]
        require(1 <= rows <= 4 and 1 <= columns <= 4 and elements >= 0 and parameter["type"] in TYPES, "Unsupported compute constant shape")
        size = rows * 16 * max(1, elements) if rows > 1 else elements * 16 if elements else columns * 4
        offset = parameter["offset"]
        require(offset % 4 == 0 and offset + size <= buffer["byteSize"], "Compute constant exceeds cbuffer")
        words = set(range(offset, offset + size, 4))
        require(not words & occupied, "Overlapping compute constants")
        occupied |= words
        variables.append({"name": parameter["name"], "offset": offset, "size": size, "class": "matrix_rows" if rows > 1 else "scalar" if columns == 1 else "vector", "type": TYPES[parameter["type"]], "rows": rows, "columns": columns, "elements": elements})
    # Unused register holes need real std140 space, not guessed material properties.
    for offset in range(0, buffer["byteSize"], 4):
        if offset not in occupied:
            variables.append({"name": f"_unity_padding_{slot}_{offset}", "offset": offset, "size": 4, "class": "scalar", "type": "uint", "rows": 1, "columns": 1, "elements": 0})
    return {"name": buffer["name"], "binding": slot, "size": buffer["byteSize"], "variables": sorted(variables, key=lambda item: item["offset"])}


def reflection(shader_name, variant, kernel, decoded):
    buffers = []
    require(len(kernel["cbVariantIndices"]) == len(kernel["cbs"]), "Cbuffer variant map mismatch")
    for index, binding in zip(kernel["cbVariantIndices"], kernel["cbs"]):
        require(0 <= index < len(variant["constantBuffers"]), "Cbuffer variant index outside table")
        buffer = variant["constantBuffers"][index]
        require(buffer["name"] == binding["name"] and decoded["buffers"].get(binding["bindPoint"], 1 << 30) <= buffer["byteSize"], "Cbuffer metadata/declaration mismatch")
        buffers.append(semantic_buffer(buffer, binding["bindPoint"]))
    require({b["binding"] for b in buffers} == set(decoded["buffers"]), "Cbuffer semantics missing")
    actual = {}
    for group, field in [("texture", "textures"), ("texture", "inBuffers"), ("uav", "outBuffers")]:
        for item in kernel[field]:
            key = (group, item["bindPoint"])
            require(key in decoded["resources"] and key not in actual, f"Unmatched compute resource: {item['name']}")
            declaration = decoded["resources"][key]
            resource = {**declaration, "name": item["name"]}
            if declaration["kind"] == "typed":
                require(item["texDimension"] in DIMENSION and declaration["dimension"] == DIMENSION[item["texDimension"]], "Texture dimension mismatch")
            else:
                require(item["texDimension"] == -1, "Buffer metadata expects a texture")
            if group == "uav" and declaration["kind"] == "typed":
                image_format = IMAGE_FORMATS.get((shader_name, item["name"]))
                require(image_format is not None, f"UAV image format producer not yet mapped: {shader_name}/{item['name']}")
                require((image_format, declaration["return_type"]) in {("rgba16f", 5), ("r32f", 5), ("r32i", 3)}, "Unsupported proven image format")
                resource["image_format"] = image_format
                resource["rdef_name"] = item["name"] + ("_mediump" if image_format == "rgba16f" else "_highp")
                resource["components"] = 4 if image_format == "rgba16f" else 1
            actual[key] = resource
    sampler_meta = {item["bindPoint"]: item for item in kernel["builtinSamplers"]}
    for key, declaration in decoded["resources"].items():
        if key[0] != "sampler":
            require(key in actual, "DXBC resource lacks semantic metadata")
            continue
        require(key[1] in sampler_meta, "Sampler state semantics missing")
        actual[key] = {**declaration, "name": "sampler" + str(key[1]), "sampler_state": sampler_meta[key[1]]["sampler"]}
    return {"constantBuffers": buffers, "resources": list(actual.values())}


def rdef_for(metadata, encode_precision=False):
    data = bytearray(build_rdef({"constantBuffers": metadata["constantBuffers"]}, 5))
    buffer_count, buffer_table, resource_count, resource_table = struct.unpack_from("<4I", data)
    buffer_rows = bytes(data[buffer_table:buffer_table + buffer_count * 24])
    resource_rows = bytearray(data[resource_table:resource_table + resource_count * 32])
    extra_buffers = []
    def string(value):
        at = len(data)
        data.extend(value.encode() + b"\0")
        data.extend(bytes(-len(data) % 4))
        return at
    # HLSLcc's suffix parser underflows on unsuffixed names of length 4/5/7.
    # An explicit precision suffix avoids that upstream bug without patching it.
    if encode_precision:
        for index, buffer in enumerate(metadata["constantBuffers"]):
            struct.pack_into("<I", resource_rows, index * 32, string(buffer["name"] + "_highp"))
    for resource in metadata["resources"]:
        group, kind = resource["group"], resource["kind"]
        name = resource.get("rdef_name", resource["name"] + "_highp") if encode_precision else resource["name"]
        typ = 3 if group == "sampler" else (2 if group == "texture" else 4) if kind == "typed" else (5 if group == "texture" else 6) if kind == "structured" else (7 if group == "texture" else 8)
        if resource.get("counter"):
            typ = {"append": 9, "consume": 10}[resource["counter"]]
        dimension = resource.get("dimension", 1 if kind in {"raw", "structured"} else 0)
        result = resource.get("return_type", 0)
        flags = (resource.get("components", 1) - 1) << 2 if group == "uav" and kind == "typed" else 0
        resource_rows.extend(struct.pack("<8I", string(name), typ, result, dimension, 0xffffffff, resource["slot"], 1, flags))
        if kind == "structured":
            element_name = string("$Element")
            type_at = len(data)
            data.extend(struct.pack("<6HI", 0, 19, 1, 1, 0, 0, 0))
            variable_at = len(data)
            data.extend(struct.pack("<10I", element_name, 0, resource["stride"], 0, type_at, 0, 0, 0, 0, 0))
            extra_buffers.append(struct.pack("<6I", string(resource["name"]), 1, variable_at, resource["stride"], 0, 3))
    table_at = len(data)
    data.extend(buffer_rows + b"".join(extra_buffers))
    resources_at = len(data)
    data.extend(resource_rows)
    struct.pack_into("<4I", data, 0, buffer_count + len(extra_buffers), table_at, len(resource_rows) // 32, resources_at)
    return bytes(data)


def validate_spirv_layout(path, metadata):
    raw = path.read_bytes()
    require(len(raw) % 4 == 0, "Unaligned validator SPIR-V")
    words = struct.unpack(f"<{len(raw) // 4}I", raw)
    require(words[0] == 0x07230203, "Invalid validator SPIR-V magic")
    names, member_names, offsets, strides, structures = {}, {}, {}, {}, {}
    at = 5
    def text(values):
        return struct.pack(f"<{len(values)}I", *values).split(b"\0", 1)[0].decode()
    while at < len(words):
        count, opcode = words[at] >> 16, words[at] & 65535
        require(count and at + count <= len(words), "Invalid validator SPIR-V instruction")
        args = words[at + 1:at + count]
        if opcode == 5:
            names[args[0]] = text(args[1:])
        elif opcode == 6:
            member_names[(args[0], args[1])] = text(args[2:])
        elif opcode == 72 and args[2] == 35:
            offsets[(args[0], args[1])] = args[3]
        elif opcode == 71 and args[1] == 6:
            strides[args[0]] = args[2]
        elif opcode == 30:
            structures[args[0]] = args[1:]
        at += count
    layouts = {}
    for buffer in metadata["constantBuffers"]:
        name = "CGlobals" if buffer["name"] == "$Globals" else buffer["name"]
        matches = [key for key in structures if names.get(key) == name]
        require(len(matches) == 1, "Validated UBO not identifiable: " + name)
        actual = {member_names[(matches[0], index)]: offsets[(matches[0], index)] for index in range(len(structures[matches[0]]))}
        size = 0
        variable_offsets = {}
        for variable in buffer["variables"]:
            name = f"hlslcc_mtx{variable['rows']}x{variable['columns']}" + variable["name"] if variable["class"].startswith("matrix_") else variable["name"]
            require(name in actual, "GLSL cbuffer member missing: " + name)
            size = max(size, actual[name] + variable["size"])
            variable_offsets[variable["name"]] = actual[name]
        layouts[buffer["name"]] = {"byte_size": (size + 15) // 16 * 16, "offsets": variable_offsets}
    for resource in metadata["resources"]:
        if resource["kind"] not in {"raw", "structured"}:
            continue
        matches = [key for key in structures if names.get(key) == resource["name"]]
        require(len(matches) == 1 and len(structures[matches[0]]) == 1, "Validated SSBO layout ambiguous")
        array = structures[matches[0]][0]
        require(strides.get(array) == resource.get("stride", 4), "GLSL std430 changed source buffer stride")
    return layouts


def convert_case(shader, variant, kernel, destination):
    code = bytes(kernel["code"])
    decoded = declarations(code)
    require(decoded["threads"] == kernel["threadGroupSize"], "Serialized/DXBC thread groups differ")
    metadata = reflection(shader, variant, kernel, decoded)
    rdef = rdef_for(metadata, encode_precision=True)
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "source.dxbc").write_bytes(code)
    (destination / "semantic.rdef").write_bytes(rdef)
    (destination / "semantic.json").write_text(json.dumps(metadata, indent=2))
    completed = subprocess.run([str(TOOL), str(destination / "source.dxbc"), str(destination / "compute.glsl"), str(destination / "reflection.json"), str(destination / "semantic.rdef")], capture_output=True, text=True, timeout=30)
    require(completed.returncode == 0, "HLSLcc failed: " + completed.stdout[-300:] + completed.stderr[-1000:])
    source = (destination / "compute.glsl").read_text()
    platform_fixes = []
    if shader == "ComputeMaximumDepthComputeShader":
        # The exact source player compiled UNITY_REVERSED_Z for D3D11. GLES3
        # produces ordinary [near=0, far=1] camera depth. Convert at the producer
        # boundary so its atomicMin and author-supplied zParameters stay valid.
        sample = re.compile(r"(?m)^(    )(u_xlat\d+\.x) = textureLod\(depthTextureTEX_with_SMPsampler0, ([^\n]+)\)\.x;$")
        matches = list(sample.finditer(source))
        require(len(matches) == 1, "Aura depth producer instruction shape changed")
        source = sample.sub(lambda m: m[0] + "\n" + m[1] + m[2] + " = 1.0 - " + m[2] + ";", source)
        platform_fixes.append("camera depth normal-Z GLES3 -> reverse-Z source contract before farthest-depth reduction")
    # HLSLcc emits this extension for every ES310 target. None is needed when
    # the original bytecode contains no typed buffer texture/image resource.
    removed_extensions = []
    if not any(r["kind"] == "typed" and r.get("dimension") == 1 for r in metadata["resources"]):
        require(not re.search(r"\b(?:[iu]?samplerBuffer|[iu]?imageBuffer)\b", source), "Unreflected buffer texture")
        extension = "#extension GL_EXT_texture_buffer : require\n"
        if extension in source:
            source = source.replace(extension, "")
            removed_extensions.append("GL_EXT_texture_buffer (no buffer texture declaration or use)")
    (destination / "compute.glsl").write_text(source)
    reflected = json.loads((destination / "reflection.json").read_text())
    require(not any(item.get("used") and item.get("name", "").startswith("_unity_padding_") for item in reflected["events"]), "DXBC reads unknown cbuffer padding semantics")
    for buffer in metadata["constantBuffers"]:
        for parameter in buffer["variables"]:
            if parameter["name"].startswith("_unity_padding_"):
                require(len(re.findall(r"\b" + re.escape(parameter["name"]) + r"\b", source)) <= 1, "Translated DXBC reads unnamed cbuffer padding")
    group = re.search(r"layout\(local_size_x\s*=\s*(\d+),\s*local_size_y\s*=\s*(\d+),\s*local_size_z\s*=\s*(\d+)\)", source)
    require(group and [int(n) for n in group.groups()] == decoded["threads"], "GLSL thread-group mismatch")
    validation = subprocess.run([str(VALIDATOR), "--target-env=opengl", "-fshader-stage=compute", "-fauto-map-locations", "-fauto-bind-uniforms", str(destination / "compute.glsl"), "-o", str(destination / "compute.spv")], capture_output=True, text=True, timeout=30)
    require(validation.returncode == 0, "GLSL validator rejected: " + validation.stderr[-1800:])
    layouts = validate_spirv_layout(destination / "compute.spv", metadata)
    (destination / "validation.txt").write_text(completed.stdout + completed.stderr + validation.stdout + validation.stderr)
    updated = copy.deepcopy(kernel)
    combined_samplers = []
    textures = []
    for texture in kernel["textures"]:
        matches = []
        for generated in reflected["textureSamplers"]:
            match = re.fullmatch(re.escape(texture["name"]) + r"TEX_with_SMPsampler(\d+)", generated)
            if match:
                slot = int(match[1])
                require(any(s["bindPoint"] == slot for s in kernel["builtinSamplers"]), "Combined sampler state is not a known builtin sampler")
                matches.append({**texture, "generatedName": generated, "samplerBindPoint": slot})
                combined_samplers.append(generated)
        # A shader may both sample and texelFetch a texture. HLSLcc leaves a
        # separate original-name uniform for the latter; preserve that binding.
        direct_used = len(re.findall(r"\b" + re.escape(texture["name"]) + r"\b", source)) > 1
        if direct_used or not matches:
            textures.append({**texture, "generatedName": texture["name"]})
        textures.extend(matches)
    require(set(combined_samplers) == set(reflected["textureSamplers"]), "Combined sampler has no original semantic texture")
    updated["textures"] = textures
    updated["code"] = list(source.encode("utf-8") + b"\0")
    updated["builtinSamplers"], _ = parallel_sampler_bindings(updated, kernel)
    require(len(updated["builtinSamplers"]) == len(updated["textures"]), "GLES per-texture sampler binding array differs")
    target_buffers = []
    for source_index in kernel["cbVariantIndices"]:
        buffer = copy.deepcopy(variant["constantBuffers"][source_index])
        layout = layouts[buffer["name"]]
        buffer["byteSize"] = layout["byte_size"]
        for parameter in buffer["params"]:
            parameter["offset"] = layout["offsets"][parameter["name"]]
        target_buffers.append(buffer)
    requirements = []
    if combined_samplers:
        requirements.append("Unity GLES generatedName + builtin samplerBindPoint runtime probe")
    if any(r.get("counter") for r in metadata["resources"]):
        requirements.append("Unity GLES separate <resource>_counter atomic buffer / CopyCount runtime probe")
    if shader == "ComputeMaximumDepthComputeShader":
        requirements.append("Aura2_Core TextureBuffers.get_OcclusionTexture RHalf -> RFloat producer patch")
    return updated, {"source_dxbc_sha256": sha(code), "rdef_sha256": sha(rdef), "glsl_sha256": sha(source.encode()), "thread_group": decoded["threads"], "max_invocations": decoded["threads"][0] * decoded["threads"][1] * decoded["threads"][2], "resource_layouts": metadata["resources"], "target_constant_buffers": target_buffers, "removed_extensions": removed_extensions, "runtime_requirements": requirements, "platform_fixes": platform_fixes}


def convert(source, output):
    require(TOOL.is_file() and VALIDATOR.is_file(), "Build the local compute tool first")
    require(output.resolve().is_relative_to(ROOT / "work"), "Compute output must stay inside this workspace's work directory")
    for filename, expected in SOURCE_HASHES.items():
        require(sha((source / filename).read_bytes()) == expected, "Original source hash mismatch: " + filename)
    target_reference = gles_target_reference()
    results, candidates, serialization_checks = [], [], []
    output.mkdir(parents=True, exist_ok=True)
    for filename in SOURCE_HASHES:
        path = source / filename
        env = UnityPy.load(str(path))
        before = {obj.path_id: sha(obj.get_raw_data()) for obj in env.objects}
        expected_trees = {}
        for obj in env.objects:
            if obj.type.name != "ComputeShader":
                continue
            tree = obj.read_typetree()
            direct = [variant for variant in tree["variants"] if variant["targetRenderer"] == 2]
            if not direct:
                continue
            require(len(direct) == 1, "Multiple D3D compute variants")
            original = direct[0]
            target = copy.deepcopy(original)
            target["targetRenderer"] = 11
            target["targetLevel"] = 3
            target["constantBuffers"] = []
            row = {"file": filename, "path_id": obj.path_id, "name": tree["m_Name"], "source_file_sha256": sha(path.read_bytes()), "kernels": [], "unsupported": []}
            row["source_object_sha256"] = before[obj.path_id]
            for index, kernel in enumerate(original["kernels"]):
                for case_index, (key, case) in enumerate(kernel["variantMap"]):
                    folder = output / f"{Path(filename).stem}_{obj.path_id}" / f"kernel_{index}_{case_index}"
                    try:
                        converted, evidence = convert_case(tree["m_Name"], original, case, folder)
                        converted["cbVariantIndices"] = []
                        for buffer in evidence["target_constant_buffers"]:
                            if buffer not in target["constantBuffers"]:
                                target["constantBuffers"].append(buffer)
                            converted["cbVariantIndices"].append(target["constantBuffers"].index(buffer))
                        target["kernels"][index]["variantMap"][case_index] = (key, converted)
                        row["kernels"].append({"index": index, "name": kernel["name"], "case": key, **evidence})
                    except (Unsupported, subprocess.TimeoutExpired) as error:
                        row["unsupported"].append({"index": index, "name": kernel["name"], "case": key, "reason": str(error)})
            destination = output / f"{Path(filename).stem}_{obj.path_id}.json"
            if not row["unsupported"]:
                candidate = copy.deepcopy(tree)
                candidate["variants"] = [variant for variant in candidate["variants"] if variant["targetRenderer"] != 11] + [target]
                destination.write_text(json.dumps(candidate), encoding="utf-8")
                row["candidate"] = str(destination)
                # ObjectReader.get_raw_data() continues to read its original
                # backing stream after an edit. save_typetree returns new bytes.
                raw = obj.save_typetree(candidate)
                raw_path = output / "objects" / f"{Path(filename).stem}_{obj.path_id}.bin"
                raw_path.parent.mkdir(exist_ok=True)
                raw_path.write_bytes(raw)
                row["object_file"] = str(raw_path.resolve())
                row["object_sha256"] = sha(raw)
                expected_trees[obj.path_id] = candidate
                candidates.append((filename, obj.path_id))
            elif destination.exists():
                destination.unlink()
            results.append(row)
            (output / "manifest.json").write_text(json.dumps({"candidates": candidates, "runtime_verified": False, "results": results, "serialization_checks": serialization_checks, "target_reference": target_reference}, indent=2))
            print(tree["m_Name"], "converted", len(row["kernels"]), "unsupported", len(row["unsupported"]), flush=True)
        saved = env.file.save()
        reopened = UnityPy.load(saved)
        objects = {obj.path_id: obj for obj in reopened.objects}
        require(objects.keys() == before.keys(), "Compute serialization changed path IDs")
        for key, obj in objects.items():
            if key in expected_trees:
                require(json.dumps(obj.read_typetree(), sort_keys=True) == json.dumps(expected_trees[key], sort_keys=True), "Compute serialized semantic roundtrip differs")
            else:
                require(sha(obj.get_raw_data()) == before[key], "Unrelated object bytes changed during compute serialization")
        require(env.file.target_platform == reopened.file.target_platform and env.file.unity_version == reopened.file.unity_version, "Compute serialization changed platform/version")
        serialization_checks.append({"file": filename, "converted_objects": len(expected_trees), "unchanged_raw_objects_verified": len(before) - len(expected_trees), "saved_bytes": len(saved), "saved_sha256": sha(saved), "path_ids_preserved": True, "semantic_roundtrip": True})
        env.file.reader.dispose()
        reopened.file.reader.dispose()
        (output / "manifest.json").write_text(json.dumps({"candidates": candidates, "runtime_verified": False, "results": results, "serialization_checks": serialization_checks, "target_reference": target_reference}, indent=2))
    for filename, expected in SOURCE_HASHES.items():
        require(sha((source / filename).read_bytes()) == expected, "Original changed during conversion: " + filename)
    return results


def selftest():
    # Normal-Z -> source reverse-Z preserves both perspective linearization
    # and farthest-depth aggregation; orthographic mapping follows the same rule.
    near, far = 0.1, 100.0
    values = [0.0, 0.1, 0.5, 0.95, 1.0]
    a, b = 1 / near - 1 / far, 1 / far
    for depth in values:
        expected = 1 / (1 / near + (1 / far - 1 / near) * depth)
        actual = 1 / (a * (1 - depth) + b)
        require(abs(expected - actual) < 1e-9, "Perspective depth contract changed")
        require(abs(((1 - (1 - depth)) * (far - near) + near) - (near + depth * (far - near))) < 1e-9, "Orthographic depth contract changed")
    require(abs(min(1 - value for value in values) - (1 - max(values))) < 1e-9, "Farthest depth reduction changed")
    env = UnityPy.load(str(SOURCE / "resources.assets"))
    obj = next(obj for obj in env.objects if obj.type.name == "ComputeShader" and obj.path_id == 1823)
    tree = obj.read_typetree()
    variant = tree["variants"][0]
    updated, result = convert_case(tree["m_Name"], variant, variant["kernels"][0]["variantMap"][0][1], ROOT / "work/compute_hlslcc_build/selftest")
    require(bytes(updated["code"]).startswith(b"#version 310 es"), "Not GLES compute")
    require(result["thread_group"] == [8, 8, 1], "Group size changed")
    require("rgba16f" in bytes(updated["code"]).decode(), "HDR UAV format not preserved")
    broken = copy.deepcopy(variant["kernels"][0]["variantMap"][0][1])
    broken["threadGroupSize"] = [1, 1, 1]
    try:
        convert_case(tree["m_Name"], variant, broken, ROOT / "work/compute_hlslcc_build/selftest_invalid")
    except Unsupported:
        pass
    else:
        raise AssertionError("Changed group size was accepted")
    print("COMPUTE_REFLECTION_SELFTEST_PASSED")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=SOURCE)
    parser.add_argument("--output", type=Path, default=ROOT / "work/android_compute")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        selftest()
    else:
        convert(args.source, args.output)
