"""Build HLSLcc reflection from explicit recovered Unity parameters, never from guessed names."""
import argparse
import json
import struct
import subprocess
from pathlib import Path

CLASSES = {"scalar": 0, "vector": 1, "matrix_rows": 2, "matrix_columns": 3, "struct": 5}
TYPES = {"void": 0, "bool": 1, "int": 2, "float": 3, "uint": 19, "half": 54}
DIMENSIONS = {"buffer": 1, "texture1d": 2, "texture1darray": 3, "texture2d": 4, "texture2darray": 5, "texture2dms": 6, "texture2dmsarray": 7, "texture3d": 8, "texturecube": 9, "texturecubearray": 10}
RETURNS = {"unorm": 1, "snorm": 2, "int": 3, "uint": 4, "float": 5, "mixed": 6, "double": 7}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def build_rdef(metadata, major=5):
    require(major in (4, 5), "Only SM4.0/5.0 RDEF is supported")
    buffers = metadata.get("constantBuffers", [])
    resources = []
    occupied = set()
    for buffer in buffers:
        slot = buffer["binding"]
        require(isinstance(slot, int) and 0 <= slot < 256 and (0, slot) not in occupied, "Invalid/duplicate cbuffer binding")
        occupied.add((0, slot))
        resources.append((buffer["name"], 0, 5, 0, slot))
    for resource in metadata.get("resources", []):
        kind = {"texture": 2, "sampler": 3}[resource["kind"]]
        slot = resource["binding"]
        require(isinstance(slot, int) and 0 <= slot < 256 and (kind, slot) not in occupied, "Invalid/duplicate resource binding")
        occupied.add((kind, slot))
        resources.append((resource["name"], kind, RETURNS[resource.get("returnType", "float")] if kind == 2 else 0, DIMENSIONS[resource["dimension"]] if kind == 2 else 0, slot))
    data = bytearray(28 + 24 * len(buffers) + 32 * len(resources))

    def string(value):
        require(isinstance(value, str) and value and "\0" not in value, "Reflection names must be nonempty strings")
        offset = len(data)
        data.extend(value.encode("utf-8") + b"\0")
        data.extend(bytes(-len(data) % 4))
        return offset

    def reserve(size):
        offset = len(data)
        data.extend(bytes(size))
        return offset

    def variable_type(variable, ancestry=()):
        require(len(ancestry) < 16 and id(variable) not in ancestry, "Cyclic/deep RDEF structure")
        cls, typ = CLASSES[variable["class"]], TYPES[variable["type"]]
        rows, columns = variable["rows"], variable["columns"]
        elements = variable.get("elements", 0)
        require(all(isinstance(v, int) for v in (rows, columns, elements)) and
                1 <= rows <= 4 and 1 <= columns <= 4 and 0 <= elements <= 4096, "Invalid variable shape")
        at = reserve(16)
        if cls == 5:
            require(typ == 0 and rows == columns == 1, "Struct class/type shape mismatch")
            members = variable.get("members", [])
            stride = variable.get("structSize")
            require(isinstance(stride, int) and 0 < stride <= 65536 and stride % 16 == 0,
                    "Invalid structure stride")
            require(isinstance(members, list) and 0 < len(members) <= 4096, "Invalid structure member count")
            member_table = reserve(len(members) * 12)
            member_names, ranges = set(), []
            for index, member in enumerate(members):
                name = member["name"]
                require(name not in member_names, "Duplicate structure member")
                member_names.add(name)
                start, size = member["offset"], member["size"]
                require(isinstance(start, int) and isinstance(size, int) and start >= 0 and start % 4 == 0 and
                        size > 0 and size % 4 == 0 and start + size <= stride, "Structure member exceeds stride")
                require(all(start + size <= a or start >= b for a, b in ranges), "Overlapping structure members")
                ranges.append((start, start + size))
                member_name = string(name)
                member_type, extent = variable_type(member, ancestry + (id(variable),))
                require(size >= extent, "Structure member smaller than its type")
                struct.pack_into("<3I", data, member_table + index * 12, member_name, member_type, start)
            struct.pack_into("<6HI", data, at, cls, typ, rows, columns, elements, len(members), member_table)
            return at, stride * max(elements, 1)
        require(typ != 0 and not variable.get("members"), "Non-struct member/type mismatch")
        require((cls == 0 and rows == columns == 1) or (cls == 1 and rows == 1) or cls in (2, 3), "Class/shape mismatch")
        vector_count, components = (rows, columns) if cls == 2 else ((columns, rows) if cls == 3 else (1, columns))
        extent = (vector_count - 1) * 16 + components * 4
        if elements > 1:
            extent += ((extent + 15) // 16) * 16 * (elements - 1)
        struct.pack_into("<6HI", data, at, cls, typ, rows, columns, elements, 0, 0)
        return at, extent

    struct.pack_into("<6I", data, 0, len(buffers), 28, len(resources), 28 + 24 * len(buffers), major << 4, 0)
    names = set()
    for index, buffer in enumerate(buffers):
        name = buffer["name"]
        require(name not in names, "Duplicate cbuffer name")
        names.add(name)
        buffer_size = buffer["size"]
        require(isinstance(buffer_size, int) and 0 < buffer_size <= 65536 and buffer_size % 16 == 0, "Invalid cbuffer size")
        variables = buffer["variables"]
        name_at = string(name)
        variable_table = reserve((40 if major == 5 else 24) * len(variables))
        ranges = []
        variable_names = set()
        for var_index, variable in enumerate(variables):
            variable_name = variable["name"]
            require(variable_name not in variable_names, "Duplicate variable in cbuffer")
            variable_names.add(variable_name)
            start, size = variable["offset"], variable["size"]
            require(isinstance(start, int) and isinstance(size, int) and start >= 0 and start % 4 == 0 and size > 0 and size % 4 == 0 and start + size <= buffer_size, "Variable exceeds cbuffer")
            require(all(start + size <= other_start or start >= other_end for other_start, other_end in ranges), "Overlapping variables")
            ranges.append((start, start + size))
            variable_name_at = string(variable_name)
            type_at, extent = variable_type(variable)
            require(size >= extent, "Variable size is smaller than its declared shape")
            struct.pack_into("<6I", data, variable_table + var_index * (40 if major == 5 else 24), variable_name_at, start, size, 0, type_at, 0)
        struct.pack_into("<6I", data, 28 + index * 24, name_at, len(variables), variable_table, buffer_size, 0, 0)
    for index, (name, kind, result, dimension, slot) in enumerate(resources):
        at = 28 + 24 * len(buffers) + 32 * index
        struct.pack_into("<8I", data, at, string(name), kind, result, dimension, 0xffffffff, slot, 1, 0)
    return bytes(data)


def shader_model(data):
    require(len(data) >= 32 and data[:4] == b"DXBC", "Not DXBC")
    count = struct.unpack_from("<I", data, 28)[0]
    require(32 + count * 4 <= len(data), "Truncated DXBC table")
    for index in range(count):
        at = struct.unpack_from("<I", data, 32 + index * 4)[0]
        require(at + 8 <= len(data), "Chunk offset outside DXBC")
        if data[at:at + 4] in (b"SHDR", b"SHEX"):
            require(at + 12 <= len(data), "Truncated shader version")
            version = struct.unpack_from("<I", data, at + 8)[0]
            require(version & 15 == 0, "Only SM4.0/5.0 is supported")
            return (version >> 4) & 15
    raise ValueError("No shader code chunk")


def self_test():
    example = {"constantBuffers": [{"name": "UnityPerDraw", "binding": 0, "size": 64, "variables": [{"name": "unity_ObjectToWorld", "offset": 0, "size": 64, "class": "matrix_columns", "type": "float", "rows": 4, "columns": 4}]}]}
    data = build_rdef(example)
    assert struct.unpack_from("<I", data)[0] == 1 and b"unity_ObjectToWorld\0" in data
    example["constantBuffers"][0]["variables"][0]["size"] = 68
    try:
        build_rdef(example)
    except ValueError:
        pass
    else:
        raise AssertionError("Out-of-buffer variable accepted")
    structure = {"name": "unity_Builtins0Array", "offset": 0, "size": 256, "class": "struct", "type": "void",
        "rows": 1, "columns": 1, "elements": 2, "structSize": 128, "members": [
        {"name": "unity_ObjectToWorldArray", "offset": 0, "size": 64, "class": "matrix_rows", "type": "float", "rows": 4, "columns": 4},
        {"name": "unity_WorldToObjectArray", "offset": 64, "size": 64, "class": "matrix_rows", "type": "float", "rows": 4, "columns": 4}]}
    metadata = {"constantBuffers": [{"name": "UnityInstancing_PerDraw0", "binding": 4, "size": 256, "variables": [structure]}]}
    data = build_rdef(metadata)
    variables = struct.unpack_from("<I", data, 28 + 8)[0]
    type_at = struct.unpack_from("<I", data, variables + 16)[0]
    assert struct.unpack_from("<6H", data, type_at) == (5, 0, 1, 1, 2, 2)
    members = struct.unpack_from("<I", data, type_at + 12)[0]
    assert struct.unpack_from("<I", data, members + 12 + 8)[0] == 64
    structure["members"][1]["offset"] = 0
    try:
        build_rdef(metadata)
    except ValueError:
        pass
    else:
        raise AssertionError("Overlapping structure members accepted")
    cycle = {"name": "cycle", "offset": 0, "size": 16, "class": "struct", "type": "void",
        "rows": 1, "columns": 1, "elements": 0, "structSize": 16}
    cycle["members"] = [cycle]
    metadata["constantBuffers"][0]["variables"] = [cycle]
    try:
        build_rdef(metadata)
    except ValueError:
        pass
    else:
        raise AssertionError("Cyclic/invalid structure accepted")
    print("PASS: semantic RDEF, struct-array member tables, invalid extents/overlaps/cycles rejected")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("dxbc", type=Path, nargs="?")
    parser.add_argument("metadata", type=Path, nargs="?")
    parser.add_argument("glsl", type=Path, nargs="?")
    parser.add_argument("reflection", type=Path, nargs="?")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    else:
        require(all((args.dxbc, args.metadata, args.glsl, args.reflection)), "Expected dxbc metadata.json output.glsl reflection.json")
        metadata = json.loads(args.metadata.read_text(encoding="utf-8-sig"))
        blob = build_rdef(metadata, shader_model(args.dxbc.read_bytes()))
        output = args.reflection.with_suffix(".rdef")
        output.write_bytes(blob)
        needs_ubo = metadata.get("requiresUniformBufferObjects", False)
        tool = Path(__file__).resolve().parents[2] / "work/hlslcc_build" / ("hlslcc_tool_ubo.exe" if needs_ubo else "hlslcc_tool.exe")
        require(tool.is_file(), "Required HLSLcc mode executable missing: " + str(tool))
        subprocess.run([str(tool), str(args.dxbc), str(args.glsl), str(args.reflection), str(output)] +
                       (["--ubo"] if needs_ubo else []), check=True)
