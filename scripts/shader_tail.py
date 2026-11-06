"""Read Unity 201806140 GPU program records, including semantic binding metadata."""

import struct


class Reader:
    def __init__(self, data):
        self.data = memoryview(data)
        self.pos = 0

    def take(self, size):
        if size < 0 or self.pos + size > len(self.data):
            raise ValueError(f"Shader record bounds at {self.pos}: {size} bytes")
        value = self.data[self.pos:self.pos + size]
        self.pos += size
        return value

    def integer(self):
        return struct.unpack("<i", self.take(4))[0]

    def count(self):
        count = self.integer()
        if count < 0 or count > (len(self.data) - self.pos) // 4:
            raise ValueError(f"Invalid shader count {count} at {self.pos - 4}")
        return count

    def align(self):
        self.take((-self.pos) % 4)

    def string(self):
        value = bytes(self.take(self.integer())).decode("utf-8")
        self.align()
        return value


def read_parameter(reader):
    name = reader.string()
    fields = ("type", "rows", "columns", "matrix", "array_size", "byte_offset")
    return {"name": name, **{key: reader.integer() for key in fields}}


def read_tail(reader):
    source_map = reader.integer()
    channels = [{"source": reader.integer(), "target": reader.integer()}
                for _ in range(reader.count())]
    groups = []
    for _ in range(reader.count()):
        group = {"name": reader.string(), "used_size": reader.integer()}
        group["parameters"] = [read_parameter(reader) for _ in range(reader.count())]
        structures = []
        for _ in range(reader.count()):
            value = {"name": reader.string(), "byte_offset": reader.integer(),
                     "array_size": reader.integer(), "struct_size": reader.integer()}
            value["parameters"] = [read_parameter(reader) for _ in range(reader.count())]
            structures.append(value)
        group["structures"] = structures
        groups.append(group)
    resources = []
    for _ in range(reader.count()):
        value = {"name": reader.string(), "type": reader.integer(),
                 "index": reader.integer(), "extra": reader.integer()}
        if value["type"] == 0:
            flags = reader.integer()
            value.update(multisampled=bool(flags & 1), dimension=(flags & 0xffffffff) >> 1)
        elif value["type"] not in (1, 2, 3, 4):
            raise ValueError(f"Unknown shader resource type: {value['type']}")
        resources.append(value)
    return {"source_map": source_map, "channels": channels, "groups": groups,
            "resources": resources}


def parse_record(data):
    reader = Reader(data)
    version = reader.integer()
    if version != 201806140:
        raise ValueError(f"Unsupported GPU program version: {version}")
    program_type = reader.integer()
    stats = [reader.integer() for _ in range(4)]
    keywords = [reader.string() for _ in range(reader.count())]
    local_keywords = [reader.string() for _ in range(reader.count())]
    code = bytes(reader.take(reader.integer()))
    reader.align()
    tail_offset = reader.pos
    tail = read_tail(reader)
    if reader.pos != len(reader.data):
        raise ValueError(f"Shader trailing bytes: {len(reader.data) - reader.pos}")
    return {"version": version, "program_type": program_type, "stats": stats,
            "keywords": keywords, "local_keywords": local_keywords,
            "program_code": code, "tail_offset": tail_offset, "tail": tail}


def self_test():
    def integer(value):
        return struct.pack("<i", value)

    def string(value):
        encoded = value.encode()
        return integer(len(encoded)) + encoded + b"\0" * ((-len(encoded)) % 4)

    record = b"".join(map(integer, [201806140, 4, 0, 0, 0, 0, 0, 0, 0]))
    record += b"".join(map(integer, [1, 0, 1]))
    record += string("test_constants") + integer(64) + integer(1)
    record += string("test_transform") + b"".join(map(integer, [0, 4, 4, 1, 0, 0]))
    record += integer(0) + integer(1)
    record += string("test_texture") + b"".join(map(integer, [0, 2, 2, 4]))
    parsed = parse_record(record)
    assert parsed["tail"]["groups"][0]["parameters"][0]["name"] == "test_transform"
    assert parsed["tail"]["resources"][0]["dimension"] == 2
    assert parsed["tail"]["resources"][0]["index"] == 2
    try:
        parse_record(record[:-1])
    except ValueError:
        pass
    else:
        raise AssertionError("Truncated shader record accepted")
    print("Unity GPU program metadata self-test passed")


if __name__ == "__main__":
    self_test()
