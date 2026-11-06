import argparse
import json
import struct
from pathlib import Path

import UnityPy
import lz4.block
from UnityPy.export.ShaderConverter import ShaderSubProgram
from UnityPy.streams import EndianBinaryReader


def extract(asset, name, output):
    for obj in UnityPy.load(str(asset)).objects:
        if obj.type.name != "Shader":
            continue
        shader = obj.read()
        if shader.m_ParsedForm.m_Name != name:
            continue
        target = next(i for i, platform in enumerate(shader.platforms) if platform == 4)
        segments = [
            lz4.block.decompress(bytes(shader.compressedBlob)[offset:offset + size], uncompressed_size=raw_size)
            for offset, size, raw_size in zip(shader.offsets[target], shader.compressedLengths[target], shader.decompressedLengths[target])
        ]
        entries = struct.unpack_from("<I", segments[0])[0]
        assert 4 + entries * 12 <= len(segments[0]), "Subprogram entry table exceeds first segment"
        output.mkdir(parents=True, exist_ok=True)
        results = []
        for index in range(min(4, entries)):
            offset, size, segment = struct.unpack_from("<3I", segments[0], 4 + index * 12)
            assert segment < len(segments) and offset + size <= len(segments[segment])
            reader = EndianBinaryReader(segments[segment], endian="<")
            reader.Position = offset
            program = ShaderSubProgram(reader)
            code = bytes(program.m_ProgramCode)
            start = code.find(b"DXBC")
            assert start >= 0, "Selected program has no DXBC"
            length = struct.unpack_from("<I", code, start + 24)[0]
            assert start + length <= len(code)
            path = output / f"program{index}.dxbc"
            path.write_bytes(code[start:start + length])
            results.append({"file": str(path), "index": index, "segment": segment, "offset": offset, "size": size, "dxbcSize": length, "programType": int(program.m_ProgramType), "keywords": program.m_Keywords, "localKeywords": program.m_LocalKeywords})
        manifest = {"asset": str(asset.resolve()), "shader": name, "pathId": obj.path_id, "entryCount": entries, "programs": results}
        (output / "source.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        print(json.dumps(manifest))
        return
    raise ValueError(f"Shader not found: {name}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("asset", type=Path)
    parser.add_argument("shader")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    extract(args.asset, args.shader, args.output)
