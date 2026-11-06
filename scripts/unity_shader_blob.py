"""Unity 2020 shader segment/record reader and recovered semantic reflection adapter."""

import struct
from shader_tail import parse_record


def value(obj, key, default=None):
    return obj.get(key, default) if isinstance(obj, dict) else getattr(obj, key, default)


def read_programs(shader, platform=4):
    import lz4.block
    platforms = list(value(shader, "platforms"))
    if platform not in platforms:
        return []
    index = platforms.index(platform)
    blob = bytes(value(shader, "compressedBlob"))
    segments = []
    for offset, size, output_size in zip(value(shader, "offsets")[index],
                                         value(shader, "compressedLengths")[index],
                                         value(shader, "decompressedLengths")[index]):
        if min(offset, size, output_size) < 0 or offset + size > len(blob):
            raise ValueError("Shader compressed segment outside blob")
        decoded = lz4.block.decompress(blob[offset:offset + size], uncompressed_size=output_size)
        if len(decoded) != output_size:
            raise ValueError("Shader segment decompressed size mismatch")
        segments.append(decoded)
    if not segments or len(segments[0]) < 4:
        raise ValueError("Shader program directory missing")
    count = struct.unpack_from("<I", segments[0])[0]
    if 4 + count * 12 > len(segments[0]):
        raise ValueError("Shader program directory outside first segment")
    result = []
    for index in range(count):
        offset, size, segment = struct.unpack_from("<III", segments[0], 4 + index * 12)
        if segment >= len(segments) or offset + size > len(segments[segment]):
            raise ValueError(f"Shader record {index} outside segment")
        raw = segments[segment][offset:offset + size]
        parsed = parse_record(raw)
        result.append({**parsed, "index": index, "segment": segment,
                       "offset": offset, "length": size, "record_bytes": raw,
                       "tail_bytes": raw[parsed["tail_offset"]:]})
    return result


def integer(number):
    return struct.pack("<I", number & 0xffffffff)


def string(text):
    encoded = text.encode("utf-8")
    return integer(len(encoded)) + encoded + b"\0" * (-len(encoded) % 4)


def encode_tail(tail):
    def parameter(param):
        return string(param["name"]) + b"".join(integer(param[key]) for key in
            ("type", "rows", "columns", "matrix", "array_size", "byte_offset"))
    output = integer(tail["source_map"]) + integer(len(tail["channels"]))
    output += b"".join(integer(channel["source"]) + integer(channel["target"])
                       for channel in tail["channels"])
    output += integer(len(tail["groups"]))
    for group in tail["groups"]:
        output += string(group["name"]) + integer(group["used_size"]) + integer(len(group["parameters"]))
        output += b"".join(parameter(param) for param in group["parameters"])
        output += integer(len(group["structures"]))
        for structure in group["structures"]:
            output += string(structure["name"]) + b"".join(integer(structure[key]) for key in
                ("byte_offset", "array_size", "struct_size")) + integer(len(structure["parameters"]))
            output += b"".join(parameter(param) for param in structure["parameters"])
    output += integer(len(tail["resources"]))
    for resource in tail["resources"]:
        output += string(resource["name"]) + b"".join(integer(resource[key]) for key in
            ("type", "index", "extra"))
        if resource["type"] == 0:
            output += integer((resource["dimension"] << 1) | resource["multisampled"])
    return output


def encode_record(record):
    output = b"".join(integer(n) for n in [record["version"], record["program_type"], *record["stats"]])
    for keywords in (record["keywords"], record["local_keywords"]):
        output += integer(len(keywords)) + b"".join(string(word) for word in keywords)
    code = record["program_code"]
    output += integer(len(code)) + code + b"\0" * (-len(code) % 4)
    return output + encode_tail(record["tail"])


def semantic_metadata(record, subprogram, common_parameters, name_indices):
    """Merge binary CB slots with serialized semantic variables; never invent property names."""
    names = {int(index): name for name, index in
             (name_indices.items() if isinstance(name_indices, dict) else name_indices)}

    def name(param):
        index = value(param, "m_NameIndex")
        if index not in names:
            raise ValueError(f"Shader semantic name index missing: {index}")
        return names[index]

    def serialized_parameter(param, is_matrix):
        return {"name": name(param), "type": value(param, "m_Type"),
            "rows": value(param, "m_RowCount", 1) if is_matrix else 1,
            "columns": 4 if is_matrix else value(param, "m_Dim"), "matrix": int(is_matrix),
            "array_size": value(param, "m_ArraySize"), "byte_offset": value(param, "m_Index")}

    def serialized_structure(param):
        offset = value(param, "m_Index")
        if offset is None:
            offset = value(param, "m_OffsetInConstantBuffer")
        return {"name": name(param), "byte_offset": offset,
            "array_size": value(param, "m_ArraySize"), "struct_size": value(param, "m_StructSize"),
            "parameters": [serialized_parameter(member, matrix) for matrix, field in
                ((False, "m_VectorMembers"), (True, "m_MatrixMembers")) for member in value(param, field, [])]}

    groups = {group["name"]: {**group, "parameters": list(group["parameters"]),
                              "structures": list(group["structures"])} for group in record["tail"]["groups"]}
    slots = {resource["name"]: resource["index"] for resource in record["tail"]["resources"] if resource["type"] == 1}
    recovered_resources = list(record["tail"]["resources"])
    parameters = [common_parameters, value(subprogram, "m_Parameters", {})]
    for block in parameters:
        for texture in value(block, "m_TextureParams", []):
            texture_name = name(texture)
            recovered = {"name": texture_name, "type": 0, "index": value(texture, "m_Index"),
                "extra": value(texture, "m_SamplerIndex"), "dimension": value(texture, "m_Dim"),
                "multisampled": bool(value(texture, "m_MultiSampled", False))}
            prior = next((item for item in recovered_resources if item["type"] == 0 and item["name"] == texture_name), None)
            if prior is None:
                recovered_resources.append(recovered)
            elif prior != recovered:
                raise ValueError(f"Inconsistent recovered texture binding: {texture_name}")
        for sampler in value(block, "m_Samplers", []):
            # Unity 2020 SamplerParameter uses lower-case names in its serialized
            # schema; keep the earlier adapter spelling for explicit fixtures.
            index = value(sampler, "bindPoint", value(sampler, "m_BindPoint"))
            state = value(sampler, "sampler", value(sampler, "m_Sampler"))
            if index is None or state is None:
                raise ValueError("Unknown serialized inline sampler layout")
            if not isinstance(index, int) or not 0 <= index < 256 or not isinstance(state, int) or not 0 <= state <= 0xffffffff:
                raise ValueError("Invalid serialized inline sampler value")
            if not any(item["type"] == 4 and item["index"] == index for item in recovered_resources):
                recovered_resources.append({"name": "", "type": 4, "index": index, "extra": state})
        if value(block, "m_BufferParams", []) or value(block, "m_UAVParams", []):
            raise ValueError("Serialized buffer/UAV requires explicit RDEF resource support")
        for binding in value(block, "m_ConstantBufferBindings", []):
            slots[name(binding)] = value(binding, "m_Index")
        for buffer in value(block, "m_ConstantBuffers", []):
            buffer_name = name(buffer)
            group = groups.setdefault(buffer_name, {"name": buffer_name,
                "used_size": value(buffer, "m_Size"), "parameters": [], "structures": []})
            # A binary variant's used-byte extent can be smaller than the serialized
            # common cbuffer declaration. Both extents are genuine source metadata;
            # retain the declaration bound needed by the union of semantic members.
            group["used_size"] = max(group["used_size"], value(buffer, "m_Size"))
            known = {param["name"] for param in group["parameters"]}
            for is_matrix, field in ((False, "m_VectorParams"), (True, "m_MatrixParams")):
                for param in value(buffer, field, []):
                    param_name = name(param)
                    if param_name in known:
                        continue
                    # Serialized matrix descriptors retain row count and four register lanes.
                    group["parameters"].append(serialized_parameter(param, is_matrix))
                    known.add(param_name)
            structures = {structure["name"]: structure for structure in group["structures"]}
            for raw_structure in value(buffer, "m_StructParams", []):
                structure = serialized_structure(raw_structure)
                previous = structures.get(structure["name"])
                if previous is not None and previous != structure:
                    raise ValueError(f"Inconsistent recovered structure: {structure['name']}")
                structures[structure["name"]] = structure
            group["structures"] = list(structures.values())
    output_buffers = []
    types = {0: "float", 1: "int", 2: "bool", 3: "half", 5: "uint"}

    def semantic_variable(param):
        rows, columns, elements = param["rows"], param["columns"], param["array_size"]
        if not 1 <= rows <= 4 or not 1 <= columns <= 4 or elements < 0:
            raise ValueError(f"Invalid variable shape: {param['name']}")
        if param["matrix"]:
            extent = rows * 16 * max(elements, 1)
            variable_class = "matrix_rows"
        else:
            extent = (max(elements, 1) - 1) * 16 + columns * 4
            variable_class = "scalar" if columns == 1 else "vector"
        if param["type"] not in types:
            raise ValueError(f"Unsupported shader value type: {param['type']}")
        return {"name": param["name"], "offset": param["byte_offset"], "size": extent,
            "class": variable_class, "type": types[param["type"]], "rows": rows,
            "columns": columns, "elements": elements}

    requires_ubo = False
    for buffer_name, group in groups.items():
        if not group["parameters"] and not group["structures"]:
            continue
        if buffer_name not in slots:
            raise ValueError(f"Constant buffer register missing: {buffer_name!r}")
        variables = [semantic_variable(param) for param in group["parameters"]]
        for structure in group["structures"]:
            stride, elements, offset = structure["struct_size"], structure["array_size"], structure["byte_offset"]
            if not isinstance(stride, int) or stride <= 0 or stride % 16 or elements < 0 or not isinstance(offset, int) or offset < 0:
                raise ValueError(f"Invalid Unity structure layout: {structure['name']}")
            members = [semantic_variable(param) for param in structure["parameters"]]
            if not members or any(member["offset"] < 0 or member["offset"] + member["size"] > stride for member in members):
                raise ValueError(f"Unity structure member exceeds stride: {structure['name']}")
            variables.append({"name": structure["name"], "offset": offset,
                "size": stride * max(elements, 1), "class": "struct", "type": "void",
                "rows": 1, "columns": 1, "elements": elements, "structSize": stride, "members": members})
            requires_ubo = True
        output_buffers.append({"name": buffer_name, "binding": slots[buffer_name],
            "size": group["used_size"], "variables": variables})
    dimensions = {1: "texture1d", 2: "texture2d", 3: "texture3d", 4: "texturecube", 5: "texture2darray", 6: "texturecubearray"}
    resources = []
    for resource in recovered_resources:
        if resource["type"] == 0:
            dimension = dimensions[resource["dimension"]]
            if resource["multisampled"]:
                dimension = {"texture2d": "texture2dms", "texture2darray": "texture2dmsarray"}[dimension]
            resources.append({"name": resource["name"], "kind": "texture",
                              "binding": resource["index"], "dimension": dimension})
            if resource["extra"] >= 0:
                # Only register identity survives stripping; this is a synthetic sampler label, never a material property.
                sampler = {"name": "sampler" + str(resource["extra"]), "kind": "sampler", "binding": resource["extra"]}
                if not any(item["kind"] == "sampler" and item["binding"] == resource["extra"] for item in resources):
                    resources.append(sampler)
        elif resource["type"] in (2, 3):
            raise ValueError("Structured/UAV resource needs explicit HLSLcc reflection support")
        elif resource["type"] == 4:
            if not any(item["kind"] == "sampler" and item["binding"] == resource["index"] for item in resources):
                resources.append({"name": "sampler" + str(resource["index"]), "kind": "sampler", "binding": resource["index"]})
    return {"constantBuffers": output_buffers, "resources": resources, "requiresUniformBufferObjects": requires_ubo,
            "rawBindings": record["tail"], "recoveredBindings": {**record["tail"],
                "groups": list(groups.values()), "resources": recovered_resources},
            "matrixLayout": "Unity register rows (vec4 lanes)"}


def self_test():
    import pathlib
    import shader_tail
    shader_tail.self_test()
    synthetic_record = {"tail": {"source_map": 0, "channels": [], "resources": [], "groups": []}}
    serialized = {"m_ConstantBufferBindings": [{"m_NameIndex": 0, "m_Index": 4}],
        "m_ConstantBuffers": [{"m_NameIndex": 0, "m_Size": 128, "m_StructParams": [
        {"m_NameIndex": 1, "m_Index": 0, "m_ArraySize": 2, "m_StructSize": 64,
         "m_MatrixMembers": [{"m_NameIndex": 2, "m_Type": 0, "m_RowCount": 4, "m_ArraySize": 0, "m_Index": 0}],
         "m_VectorMembers": []}]}]}
    metadata = semantic_metadata(synthetic_record, {}, serialized,
        [("UnityInstancing_PerDraw0", 0), ("unity_Builtins0Array", 1), ("unity_ObjectToWorldArray", 2)])
    assert metadata["requiresUniformBufferObjects"]
    structure = metadata["constantBuffers"][0]["variables"][0]
    assert structure["class"] == "struct" and structure["structSize"] == 64 and structure["size"] == 128
    assert structure["members"][0]["name"] == "unity_ObjectToWorldArray"
    assert metadata["recoveredBindings"]["groups"][0]["structures"][0]["parameters"][0]["matrix"] == 1
    fixture = pathlib.Path(__file__).resolve().parents[1] / "work/evidence/shader_format/tail-fixture.record"
    if fixture.is_file():
        raw = fixture.read_bytes()
        assert encode_record(parse_record(raw)) == raw
        print("D3D11 record lossless round-trip passed")


if __name__ == "__main__":
    self_test()
