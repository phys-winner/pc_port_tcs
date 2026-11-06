"""Prepare a private, strictly DIRECT-only Aura buffer-without-counter comparison.

The paired DIRECT resource/level hashes are mandatory. Other producer modes
still require Append/reset and must not be used with this diagnostic DLL.
No canonical file, build pipeline, STATE or device is modified.
"""
import argparse
import copy
import difflib
import json
import struct
import subprocess
from pathlib import Path

import UnityPy
from prepare_depth_precision import container_layout
from prepare_mobile_aura_integer_cells import ROOT, COMPILER, cases, require, sha

SOURCE = ROOT / 'app/assets/bin/Data/Managed/Aura2_Core.dll'
RETAINED = ROOT / 'work/aura_counter_input/Aura2_Core.dll'
RESOURCES = ROOT / 'work/aura_direct23/resources.assets'
LEVEL = ROOT / 'work/aura_direct23/level1'
WORK = ROOT / 'work/aura_counter25'
DLL_BEFORE = '7c0c666f5eabce1c46be3c476ce8f1c7e4dd2f1774af8287f39feb67ea22aa50'
RESOURCES_BEFORE = '069d13fa386fa6cf457706b8a9f0e0603a6302cafdac3c82844dde067a0d2196'
LEVEL_REQUIRED = '60d56700855bc8835d61b1297dc9e9bf293d7daa7704e255be7f535a9096bbf0'
QUALITY_REQUIRED = 'c663e2b0425a0a4ab54b399f7f15bbbffa3d1f158a28a1f1a4f15873550f6f16'
COUNTER = 'layout (binding = 0) uniform highp atomic_uint appendedCellsBuffer_counter;\n'
RESET = bytes.fromhex('027bae0000041b9a166fc500000a')
RESET_OFFSET = 0x509a
TYPE_OFFSET = 0x6802
BODY_HASHES = ('77a80f2ee1ded3138b3cbaf04f9166888c946626570d7ef4764761d5eef2853d',
               'd437506c20d61edd00ba06c3f36ca7ec1da12d80789304506e7c1e83093dfc0e')


class ManagedImage:
    """Read the fixed PE/CLI metadata needed for the two guarded MethodDefs."""

    def __init__(self, raw):
        self.raw = raw
        require(raw[:2] == b'MZ' and len(raw) > 0x100, 'PE DOS header differs')
        pe = self.u32(60)
        require(raw[pe:pe + 4] == b'PE\0\0' and self.u16(pe + 4) == 0x14c, 'PE signature/machine differs')
        optional = pe + 24
        require(self.u16(optional) == 0x10b, 'Expected PE32 CLI image')
        section_table = optional + self.u16(pe + 20)
        self.sections = []
        for i in range(self.u16(pe + 6)):
            size, va, raw_size, pointer = struct.unpack_from('<4I', raw, section_table + i * 40 + 8)
            self.sections.append((va, max(size, raw_size), pointer, raw_size))
        require(self.u32(optional + 92) >= 15, 'CLI directory missing')
        require(raw[optional + 96 + 4 * 8:optional + 96 + 5 * 8] == b'\0' * 8,
                'Authenticode-signed input is unsupported')
        cli = self.offset(self.u32(optional + 96 + 14 * 8), 40)
        require(self.u32(cli) >= 72 and self.u32(cli + 16) == 1 and
                raw[cli + 32:cli + 40] == b'\0' * 8, 'CLI flags/strong-name contract differs')
        self.metadata_offset = self.offset(self.u32(cli + 8), self.u32(cli + 12))
        self.metadata_size = self.u32(cli + 12)
        md = self.metadata_offset
        require(raw[md:md + 4] == b'BSJB', 'CLI metadata signature differs')
        cursor = md + 16 + ((self.u32(md + 12) + 3) & ~3)
        count = self.u16(cursor + 2)
        cursor += 4
        streams = {}
        for _ in range(count):
            offset, size = struct.unpack_from('<II', raw, cursor)
            start = cursor + 8
            end = raw.index(b'\0', start, min(start + 32, len(raw)))
            name = raw[start:end].decode('ascii')
            require(offset + size <= self.metadata_size and name not in streams, 'Metadata stream differs')
            streams[name] = (md + offset, size)
            cursor = (end + 4) & ~3
        require(all(s in streams for s in ('#~', '#Strings', '#Blob')), 'Expected metadata streams missing')
        self.strings, self.strings_size = streams['#Strings']
        table, table_size = streams['#~']
        heaps = raw[table + 6]
        self.string_width = 4 if heaps & 1 else 2
        guid = 4 if heaps & 2 else 2
        blob = 4 if heaps & 4 else 2
        valid = struct.unpack_from('<Q', raw, table + 8)[0]
        self.rows = {}
        cursor = table + 24
        for i in range(64):
            if valid & (1 << i):
                self.rows[i] = self.u32(cursor)
                cursor += 4
        idx = lambda t: 4 if self.rows.get(t, 0) >= 65536 else 2
        coded = lambda bits, ts: 4 if max(self.rows.get(t, 0) for t in ts) >= (1 << (16 - bits)) else 2
        resolution = coded(2, (0, 26, 35, 1))
        type_ref = coded(2, (2, 1, 27))
        s = self.string_width
        self.widths = {0: 2 + s + 3 * guid, 1: resolution + 2 * s,
                       2: 4 + 2 * s + type_ref + idx(4) + idx(6), 3: idx(4),
                       4: 2 + s + blob, 5: idx(6), 6: 8 + s + blob + idx(8),
                       7: idx(8), 8: 4 + s, 9: idx(2) + type_ref,
                       10: coded(3, (2, 1, 26, 6, 27)) + s + blob}
        self.tables = {}
        for i in range(11):
            self.tables[i] = cursor
            cursor += self.rows.get(i, 0) * self.widths[i]
        require(cursor <= table + table_size, 'Metadata table bounds differ')
        self.type_method_offset = 4 + 2 * s + type_ref + idx(4)
        self.method_index_width = idx(6)
        self.member_parent_width = coded(3, (2, 1, 26, 6, 27))

    def u16(self, offset):
        return struct.unpack_from('<H', self.raw, offset)[0]

    def u32(self, offset):
        return struct.unpack_from('<I', self.raw, offset)[0]

    def number(self, offset, width):
        return self.u32(offset) if width == 4 else self.u16(offset)

    def offset(self, rva, size):
        matches = [p + rva - va for va, extent, p, raw_size in self.sections
                   if va <= rva and rva + size <= va + min(extent, raw_size)]
        require(len(matches) == 1 and matches[0] + size <= len(self.raw), 'PE RVA bounds differ')
        return matches[0]

    def row(self, table, rid):
        require(0 < rid <= self.rows.get(table, 0), 'Metadata RID differs')
        return self.tables[table] + (rid - 1) * self.widths[table]

    def string(self, index):
        require(0 <= index < self.strings_size, 'String heap index differs')
        end = self.raw.index(b'\0', self.strings + index, self.strings + self.strings_size)
        return self.raw[self.strings + index:end].decode('utf-8')

    def method(self, rid, name, rva, size, local_sig):
        row = self.row(6, rid)
        require(self.string(self.number(row + 8, self.string_width)) == name and self.u32(row) == rva,
                'MethodDef identity/RVA differs: ' + name)
        header = self.offset(rva, 12 + size)
        require(struct.unpack_from('<HHII', self.raw, header) == (0x3013, 5, size, local_sig),
                'Method body header differs: ' + name)
        return header + 12, self.raw[header + 12:header + 12 + size]


def inspect_before(raw):
    image = ManagedImage(raw)
    row = image.row(2, 0x11)
    require(image.string(image.number(row + 4, image.string_width)) == 'Frustum' and
            image.string(image.number(row + 4 + image.string_width, image.string_width)) == 'Aura2API',
            'Declaring type differs')
    first = image.number(row + image.type_method_offset, image.method_index_width)
    next_row = image.row(2, 0x12)
    last = image.number(next_row + image.type_method_offset, image.method_index_width)
    require(first <= 0x92 < 0x95 < last, 'Method ownership differs')
    field = image.row(4, 0xae)
    require(image.string(image.number(field + 2, image.string_width)) == '_dispatchBuffers', 'Buffer field token differs')
    member = image.row(10, 0xc5)
    require(image.string(image.number(member + image.member_parent_width, image.string_width)) == 'SetCounterValue',
            'Counter call token differs')
    start_a, code_a = image.method(0x92, 'ComputeData', 0x6aa0, 6694, 0x1100000f)
    start_b, code_b = image.method(0x95, 'SetFrustumGridResolution', 0x84d4, 298, 0x11000010)
    require(start_a + 0x3ee == RESET_OFFSET and code_a[0x3ee:0x3fc] == RESET and
            code_a[0x3ec:0x3ee] == b'\x13\x07' and code_a[0x3fc] == 2, 'Reset statement anchor differs')
    require(start_b + 0x122 == TYPE_OFFSET and
            code_b[0x11d:0x12a] == bytes.fromhex('28cb0100061873e300000aa22a'), 'Append constructor anchor differs')
    require((sha(code_a), sha(code_b)) == BODY_HASHES, 'Unrelated method body bytes differ')
    return image


def verify_dll(before, after):
    require(sha(before) == DLL_BEFORE and len(after) == len(before), 'Exact DLL preimage/size required')
    image = inspect_before(before)
    changed = ManagedImage(after)
    require(after[RESET_OFFSET:RESET_OFFSET + 14] == b'\0' * 14 and after[TYPE_OFFSET] == 0x16,
            'No-counter byte replacements differ')
    inverse = bytearray(after)
    inverse[RESET_OFFSET:RESET_OFFSET + 14] = RESET
    inverse[TYPE_OFFSET] = 0x18
    require(bytes(inverse) == before, 'Complete DLL inverse differs')
    a, b = image.metadata_offset, image.metadata_offset + image.metadata_size
    require(changed.metadata_offset == a and changed.metadata_size == image.metadata_size and
            before[a:b] == after[a:b], 'Serialized metadata changed')
    changed.method(0x92, 'ComputeData', 0x6aa0, 6694, 0x1100000f)
    changed.method(0x95, 'SetFrustumGridResolution', 0x84d4, 298, 0x11000010)
    return {'resetFileOffset': RESET_OFFSET, 'resetSpanBytes': 14, 'appendTypeFileOffset': TYPE_OFFSET,
            'actualChangedBytes': sum(a != b for a, b in zip(before, after)),
            'metadataByteIdentical': True, 'allOffsetsAndLengthsPreserved': True, 'fullInverseExact': True,
            'methodTokens': ['0x06000092', '0x06000095'], 'resetRemoved': True, 'bufferType': 'Default=0'}


def patch_dll(raw):
    require(sha(raw) == DLL_BEFORE, 'Exact DLL preimage required')
    inspect_before(raw)
    after = bytearray(raw)
    after[RESET_OFFSET:RESET_OFFSET + 14] = b'\0' * 14
    after[TYPE_OFFSET] = 0x16
    result = bytes(after)
    return result, verify_dll(raw, result)


def patch_code(source):
    require(source.count(COUNTER) == 1 and source.count('appendedCellsBuffer_counter') == 1,
            'Sole unused atomic declaration required')
    require('atomicCounterIncrement' not in source and 'atomicCounterDecrement' not in source,
            'DIRECT shader must not use atomic operations')
    require(source.count('u_xlatu3 = portCell.x + uint(Aura_BufferResolution.x) * '
                         '(portCell.y + uint(Aura_BufferResolution.y) * portCell.z);') == 1,
            'Exact DIRECT indexing required')
    return source.replace(COUNTER, '')


def resource_tree(tree):
    target = copy.deepcopy(tree)
    case = cases(target)[0]
    raw = bytes(case['code'])
    require(raw.endswith(b'\0') and case['threadGroupSize'] == [8, 8, 1], 'Kernel0 identity differs')
    source = raw[:-1].decode('utf-8')
    code = patch_code(source)
    case['code'] = list(code.encode() + b'\0')
    reverse = copy.deepcopy(target)
    cases(reverse)[0]['code'] = cases(tree)[0]['code']
    require(reverse == tree, 'Unrelated shader fields changed')
    return target, source, code


def patch_resources(raw):
    require(sha(raw) == RESOURCES_BEFORE, 'Exact DIRECT resources preimage required')
    env = UnityPy.load(raw)
    try:
        obj = env.file.objects[1825]
        require(obj.type.name == 'ComputeShader', 'Shader1825 type differs')
        tree, source, code = resource_tree(obj.read_typetree())
        obj.save_typetree(tree)
        return env.file.save(), source, code
    finally:
        env.file.reader.dispose()


def verify_resources(before, after):
    require(sha(before) == RESOURCES_BEFORE, 'Exact DIRECT resources preimage required')
    original, candidate = UnityPy.load(before), UnityPy.load(after)
    try:
        require(container_layout(original.file) == container_layout(candidate.file), 'Container metadata differs')
        obj = candidate.file.objects[1825]
        expected, _, _ = resource_tree(original.file.objects[1825].read_typetree())
        require(obj.read_typetree() == expected, 'Declaration-only patch differs')
        for pid, item in original.file.objects.items():
            if pid != 1825:
                require(item.get_raw_data() == candidate.file.objects[pid].get_raw_data(),
                        'Unrelated raw object differs: ' + str(pid))
        result_hash = sha(obj.get_raw_data())
        obj.set_raw_data(original.file.objects[1825].get_raw_data())
        require(candidate.file.save() == before, 'Complete resources inverse differs')
        return {'otherRawObjectsPreserved': len(original.file.objects) - 1, 'kernel0Only': True,
                'metadataAndAllExecutableStatementsExact': True, 'fullInverseExact': True,
                'object1825AfterSha256': result_hash}
    finally:
        original.file.reader.dispose()
        candidate.file.reader.dispose()


def asset_boundary(resources, level):
    require(sha(resources) == RESOURCES_BEFORE and sha(level) == LEVEL_REQUIRED, 'Matched DIRECT assets required')
    env = UnityPy.load(level)
    try:
        raw = env.file.objects[36456].get_raw_data()
        require(sha(raw) == QUALITY_REQUIRED and struct.unpack_from('<3i', raw, 68) == (64, 40, 64) and
                struct.unpack_from('<i', raw, 172)[0] == 0 and struct.unpack_from('<i', raw, 180)[0] == 0,
                'DIRECT temporal/occlusion/grid contract differs')
    finally:
        env.file.reader.dispose()


def guard_tests(dll, patched_dll, resources, patched_resources, level, source):
    passed = []

    def reject(name, action, message):
        try:
            action()
        except ValueError as error:
            require(message in str(error), 'Wrong negative rejection: ' + name + ': ' + str(error))
            passed.append(name)
            return
        raise AssertionError('Invalid candidate accepted: ' + name)

    def replace(raw, offset, data):
        return raw[:offset] + data + raw[offset + len(data):]

    reject('wrong_dll_preimage', lambda: patch_dll(dll[:-1]), 'preimage')
    reject('already_patched_dll', lambda: patch_dll(patched_dll), 'preimage')
    reject('wrong_pe_header', lambda: inspect_before(b'NZ' + dll[2:]), 'DOS header')
    image = ManagedImage(dll)
    reject('wrong_method_rva', lambda: inspect_before(replace(dll, image.row(6, 0x92), struct.pack('<I', 0x6aa4))),
           'MethodDef identity/RVA')
    reject('wrong_method_header', lambda: inspect_before(replace(dll, 0x4ca2, b'\x04\0')), 'body header')
    reject('wrong_reset_statement', lambda: inspect_before(replace(dll, RESET_OFFSET, b'\x00')), 'Reset statement')
    reject('wrong_append_enum', lambda: inspect_before(replace(dll, TYPE_OFFSET, b'\x17')), 'Append constructor')
    reject('unrelated_body_change', lambda: inspect_before(replace(dll, 0x4cac, bytes([dll[0x4cac] ^ 1]))),
           'Unrelated method body')
    reject('unrelated_dll_change', lambda: verify_dll(dll, replace(patched_dll, len(dll) - 1,
           bytes([patched_dll[-1] ^ 1]))), 'DLL inverse')
    reject('mismatched_level_contract', lambda: asset_boundary(resources, level[:-1]), 'Matched DIRECT')
    reject('mismatched_resource_contract', lambda: asset_boundary(resources[:-1], level), 'Matched DIRECT')
    reject('missing_declaration', lambda: patch_code(source.replace(COUNTER, '')), 'Sole unused')
    reject('duplicate_declaration', lambda: patch_code(source + COUNTER), 'Sole unused')
    reject('counter_still_used', lambda: patch_code(source + '\nuint x = atomicCounterIncrement(other);'),
           'must not use atomic')
    reject('wrong_direct_stride', lambda: patch_code(source.replace('Resolution.y) * portCell.z',
                                                                 'Resolution.x) * portCell.z')), 'DIRECT indexing')
    env = UnityPy.load(patched_resources)
    try:
        obj = env.file.objects[1826]
        raw = obj.get_raw_data()
        obj.set_raw_data(raw[:-1] + bytes([raw[-1] ^ 1]))
        bad = env.file.save()
    finally:
        env.file.reader.dispose()
    reject('changed_lighting_raw_object', lambda: verify_resources(resources, bad), 'Unrelated raw object')
    return passed


def spirv_check(raw):
    require(len(raw) % 4 == 0, 'SPIR-V length differs')
    words = struct.unpack('<' + 'I' * (len(raw) // 4), raw)
    require(words[0] == 0x07230203, 'SPIR-V magic differs')
    cursor, count = 5, 0
    while cursor < len(words):
        size, op = words[cursor] >> 16, words[cursor] & 0xffff
        require(size > 0 and cursor + size <= len(words), 'SPIR-V instruction bounds differ')
        require(op not in (*range(227, 243), 318, 319), 'SPIR-V atomic operation remains')
        require(not (op == 32 and words[cursor + 2] == 10) and
                not (op == 59 and words[cursor + 3] == 10), 'SPIR-V AtomicCounter storage remains')
        count += 1
        cursor += size
    return {'instructionCount': count, 'atomicOperations': 0, 'atomicCounterStorage': 0}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=WORK)
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    require(source in (SOURCE, RETAINED) and not args.source.is_symlink(), 'DLL source must be canonical or pinned retained input')
    require(output == WORK or output.is_relative_to(WORK), 'Output must stay in work/aura_counter25')
    require(not args.output.is_symlink() and not output.exists(), 'Output must be a fresh ordinary directory')
    dll, resources, level = source.read_bytes(), RESOURCES.read_bytes(), LEVEL.read_bytes()
    asset_boundary(resources, level)
    patched_dll, dll_receipt = patch_dll(dll)
    patched_resources, shader_before, shader_after = patch_resources(resources)
    resource_receipt = verify_resources(resources, patched_resources)
    guards = guard_tests(dll, patched_dll, resources, patched_resources, level, shader_before)
    output.mkdir(parents=True)
    comp = output / 'kernel0-no-counter.comp'
    comp.write_text(shader_after, encoding='utf-8', newline='\n')
    compiled = subprocess.run([str(COMPILER), '--target-env=opengl', '-fshader-stage=compute',
                               '-fauto-map-locations', '-fauto-bind-uniforms', str(comp),
                               '-o', str(comp.with_suffix('.spv'))], capture_output=True, text=True, timeout=30)
    require(compiled.returncode == 0, 'No-counter GLSL compile failed: ' + compiled.stderr)
    spv = spirv_check(comp.with_suffix('.spv').read_bytes())
    require(sha(source.read_bytes()) == DLL_BEFORE and sha(RESOURCES.read_bytes()) == RESOURCES_BEFORE and
            sha(LEVEL.read_bytes()) == LEVEL_REQUIRED, 'Inputs changed concurrently')
    for name, data in (('Aura2_Core.dll', patched_dll), ('resources.assets', patched_resources)):
        with (output / name).open('xb') as stream:
            stream.write(data)
    verify_dll(dll, (output / 'Aura2_Core.dll').read_bytes())
    verify_resources(resources, (output / 'resources.assets').read_bytes())
    (output / 'kernel0.diff').write_text(''.join(difflib.unified_diff(
        shader_before.splitlines(True), shader_after.splitlines(True), fromfile='kernel0-direct.comp',
        tofile='kernel0-no-counter.comp')), encoding='utf-8')
    manifest = {'format': 'closing-shift-aura-direct-without-counter-v1', 'status': 'PREPARED_ONLY',
                'scope': 'DIRECT ONLY: paired level occlusion=0 and temporal=0; other producer modes unsupported',
                'generatorSha256': sha(Path(__file__).read_bytes()), 'source': str(source),
                'dllBeforeSha256': DLL_BEFORE, 'dllAfterSha256': sha(patched_dll),
                'resourcesBeforeSha256': RESOURCES_BEFORE, 'resourcesAfterSha256': sha(patched_resources),
                'requiredLevel': str(LEVEL), 'requiredLevelSha256': LEVEL_REQUIRED,
                'dll': dll_receipt, 'resources': resource_receipt, 'negativeGuards': guards,
                'compile': {'compiler': str(COMPILER), 'exitCode': compiled.returncode,
                            'stdout': compiled.stdout, 'stderr': compiled.stderr, 'spirv': spv},
                'nativeLibraryModified': False, 'canonicalOrDeviceModified': False,
                'requiresNewIl2cppRuntimeBuild': True, 'deviceAcceptance': False}
    with (output / 'manifest.json').open('x', encoding='utf-8') as stream:
        json.dump(manifest, stream, indent=2)
        stream.write('\n')
    print(json.dumps({k: manifest[k] for k in ('status', 'dllAfterSha256', 'resourcesAfterSha256',
                                             'requiredLevelSha256', 'generatorSha256')}, indent=2))


if __name__ == '__main__':
    main()
