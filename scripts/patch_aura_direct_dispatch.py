"""Private fixed-grid comparison: replace Aura's three live indirect dispatches.

Requires the exact DIRECT/no-counter DLL, matched assets, and non-VR GLES build.
Only mono 64x40x64 is supported. Dead-branch calls and GPU data bindings remain.
"""
import argparse
import copy
import json
import re
import struct
import subprocess
from pathlib import Path

import UnityPy
from patch_aura_direct_counter import ManagedImage, require, sha
from prepare_mobile_aura_integer_cells import cases

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'app/assets/bin/Data/Managed/Aura2_Core.dll'
RETAINED = ROOT / 'work/aura_direct_dispatch_input/Aura2_Core.dll'
COUNTER_SOURCE = ROOT / 'work/aura_counter25/Aura2_Core.dll'
RESOURCES = ROOT / 'app/obb/assets/bin/Data/resources.assets'
LEVEL = ROOT / 'app/obb/assets/bin/Data/level1'
GLOBAL = ROOT / 'app/assets/bin/Data/globalgamemanagers'
WORK = ROOT / 'work/aura_direct_dispatch29'
ILSPY = Path.home() / '.dotnet/tools/ilspycmd.exe'
DLL_SHA = 'a2f1a5517829b8f639c6a785cb4e793a40830c563035d731c54dd524d6fa0d45'
RESOURCE_SHA = '7397ba75c4545dd4f733968b9cfc00a7511ba8f7367ce2eae265cc8853000b5d'
LEVEL_SHA = '0b33fb931d102859fcb5bb5971c3a6a5c0c964cfbef4dca251652552782217cb'
GLOBAL_SHA = '0f0b2fd9aeab7686c44a2b8433688bb213a4ef0ed72790f3591045c10ed97a21'
QUALITY_SHA = 'c663e2b0425a0a4ab54b399f7f15bbbffa3d1f158a28a1f1a4f15873550f6f16'
LIGHTING_SHA = 'dfe523c2ee7c6615d402fcf6c397252666cd0e68f34a56348e9ee859d56897c8'
BODY_SHA = '7202edb20ae593d3e16d3c7c07a236e4384ae63204ca9e4e2a6cd1ae390c5330'
OFFSET = 0x62cc
OLD = bytes.fromhex('027bae000004179a6fc900000a')
NEW = bytes.fromhex('1e1b1f20000000006fdd00000a')
SITES = (
    ('producer', 0x534f, 0x06a3, bytes.fromhex('027bae000004199a6fc900000a'),
     bytes.fromhex('1e1b1700000000006fdd00000a'), bytes.fromhex('027ba9000004110a'), (8, 5, 1)),
    ('lighting', OFFSET, 0x1620, OLD, NEW, bytes.fromhex('027baa0000041105'), (8, 5, 32)),
    ('accumulation', 0x65d6, 0x192a, bytes.fromhex('027bae000004199a6fc900000a'),
     bytes.fromhex('1e1b1700000000006fdd00000a'), bytes.fromhex('027baf0000041105'), (8, 5, 1)),
)


def inspect_before(raw):
    image = ManagedImage(raw)
    start, code = image.method(0x92, 'ComputeData', 0x6aa0, 6694, 0x1100000f)
    owner = image.row(2, 0x11)
    require(image.string(image.number(owner + 4, image.string_width)) == 'Frustum' and
            image.string(image.number(owner + 4 + image.string_width, image.string_width)) == 'Aura2API',
            'Frustum owner differs')
    require(image.number(owner + image.type_method_offset, image.method_index_width) <= 0x92 <
            image.number(image.row(2, 0x12) + image.type_method_offset, image.method_index_width),
            'Method ownership differs')
    for rid, name in ((0xc9, 'DispatchIndirect'), (0xdd, 'Dispatch')):
        row = image.row(10, rid)
        require(image.number(row, image.member_parent_width) == 0x161 and
                image.string(image.number(row + image.member_parent_width, image.string_width)) == name,
                'Existing compute call token differs')
    type_ref = image.row(1, 0x2c)
    name_at = type_ref + image.widths[1] - 2 * image.string_width
    require(image.string(image.number(name_at, image.string_width)) == 'ComputeShader' and
            image.string(image.number(name_at + image.string_width, image.string_width)) == 'UnityEngine',
            'ComputeShader TypeRef differs')
    for rid, name in ((0xa9, '_computeVisibleCellsComputeShader'), (0xaa, '_computeDataComputeShader'),
                      (0xaf, '_computeAccumulationComputeShader'), (0xae, '_dispatchBuffers')):
        row = image.row(4, rid)
        require(image.string(image.number(row + 2, image.string_width)) == name, 'Dispatch field token differs')
    for name, offset, il, old, new, prefix, groups in SITES:
        require(len(old) == len(new) == 13 and start + il == offset and code[il - 8:il] == prefix and
                code[il:il + 13] == old, name + ' dispatch anchor differs')
    require(code[0x103b:0x1040] == NEW[-5:], 'Existing direct call anchor differs')
    require(sha(code) == BODY_SHA, 'Unrelated ComputeData body change')
    return image


def verify_dll(before, after):
    image = inspect_before(before)
    require(len(after) == len(before), 'Candidate length differs')
    inverse = bytearray(after)
    for name, offset, il, old, new, prefix, groups in SITES:
        require(after[offset:offset + 13] == new, name + ' candidate span differs')
        inverse[offset:offset + 13] = old
    require(bytes(inverse) == before, 'DLL inverse differs')
    other = ManagedImage(after)
    require(after[image.metadata_offset:image.metadata_offset + image.metadata_size] ==
            before[image.metadata_offset:image.metadata_offset + image.metadata_size], 'Metadata differs')
    other.method(0x92, 'ComputeData', 0x6aa0, 6694, 0x1100000f)
    changed = [i for i, (a, b) in enumerate(zip(before, after)) if a != b]
    require(len(changed) == 22, 'Expected 22 changed byte values')
    return {'methodToken': '0x06000092', 'methodRva': '0x6aa0', 'codeSize': 6694, 'maxStack': 5,
            'spans': [{'name': name, 'fileOffset': hex(offset), 'ilSpan': [hex(il), hex(il + 13)],
                       'oldHex': old.hex(), 'newHex': new.hex(), 'groups': list(groups)}
                      for name, offset, il, old, new, prefix, groups in SITES],
            'changedByteOffsets': changed, 'existingDispatchMemberRef': '0x0a0000dd',
            'allOtherBytesIdentical': True, 'metadataIdentical': True, 'inverseExact': True}


def patch_dll(raw):
    require(sha(raw) == DLL_SHA, 'Exact a2 DIRECT/no-counter DLL preimage required')
    inspect_before(raw)
    result = bytearray(raw)
    for name, offset, il, old, new, prefix, groups in SITES:
        result[offset:offset + 13] = new
    result = bytes(result)
    return result, verify_dll(raw, result)


def quality_contract(raw):
    require(struct.unpack_from('<3i', raw, 68) == (64, 40, 64), 'Fixed grid must be 64x40x64')
    require(struct.unpack_from('<i', raw, 172)[0] == 0, 'Temporal reprojection must remain disabled')
    require(struct.unpack_from('<i', raw, 180)[0] == 0, 'Occlusion culling must remain disabled')
    require(struct.unpack_from('<i', raw, 148)[0] == 0, 'Denoising must remain disabled')
    require(struct.unpack_from('<i', raw, 156)[0] == 0, 'Blur must remain disabled')
    require(sha(raw) == QUALITY_SHA, 'Other quality fields differ')


def kernel_contract(tree):
    variants = [v for v in tree['variants'] if v['targetRenderer'] == 11]
    require(len(variants) == 1 and len(variants[0]['kernels']) == 12, 'Twelve GLES lighting kernels required')
    for kernel in variants[0]['kernels']:
        require(kernel['name'] == 'ComputeDataBuffer' and len(kernel['variantMap']) == 1, 'Lighting kernel identity differs')
        case = kernel['variantMap'][0][1]
        code = bytes(case['code']).decode()
        require(case['threadGroupSize'] == [8, 8, 2] and
                code.count('layout(local_size_x = 8, local_size_y = 8, local_size_z = 2) in;') == 1,
                'Lighting thread-group shape differs')
        require('dispatchSizeBuffer_buf[0].value[(0 >> 2) + 0]' in code and
                'dispatchSizeBuffer_buf[1].value[(0 >> 2) + 0]' in code,
                'Retained lighting dispatch-size data binding differs')


def mono_contract(build):
    require(build['enabledVRDevices'] == [] and build['m_GraphicsAPIs'] == [11],
            'Only the matched non-VR GLES build is supported')


def column_kernels(tree, name, indices):
    variants = [v for v in tree['variants'] if v['targetRenderer'] == 11]
    require(len(variants) == 1, 'One GLES column variant required')
    for index in indices:
        kernel = variants[0]['kernels'][index]
        require(kernel['name'] == name and len(kernel['variantMap']) == 1, 'Column kernel identity differs')
        case = kernel['variantMap'][0][1]
        require(case['threadGroupSize'] == [8, 8, 1] and
                bytes(case['code']).decode().count('layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;') == 1,
                'Column thread-group shape differs')


def asset_boundary(resources, level, global_data):
    require(sha(resources) == RESOURCE_SHA and sha(level) == LEVEL_SHA and sha(global_data) == GLOBAL_SHA,
            'Matched resources/level/non-VR build preimages required')
    env = UnityPy.load(resources)
    try:
        lighting = env.file.objects[1826]
        require(sha(lighting.get_raw_data()) == LIGHTING_SHA, 'Current lighting shader object differs')
        tree = lighting.read_typetree(); kernel_contract(tree)
        producer_object = env.file.objects[1825]; accumulation = env.file.objects[1823]
        require(sha(producer_object.get_raw_data()) == 'e5d87cd0f94ed0be5cd292ed4722c3ddbdc7a08e558fd1372f9233d3785594e0' and
                sha(accumulation.get_raw_data()) == '2d2d95131f4da81662cc135c10333739abdf3b95d856cf53f4722c11b770ffe6',
                'Producer/accumulation object differs')
        producer_tree = producer_object.read_typetree()
        column_kernels(producer_tree, 'ComputeVisibleCells', [0])
        column_kernels(accumulation.read_typetree(), 'AccumulateLighting', range(8))
        producer = bytes(cases(producer_tree)[0]['code']).decode()
        require('atomic_uint' not in producer and 'atomicCounterIncrement' not in producer and
                'portCell.x + uint(Aura_BufferResolution.x) * (portCell.y + uint(Aura_BufferResolution.y) * portCell.z)' in producer,
                'DIRECT/no-counter producer contract differs')
    finally:
        env.file.reader.dispose()
    env = UnityPy.load(level)
    try:
        quality = env.file.objects[36456].get_raw_data(); quality_contract(quality)
    finally:
        env.file.reader.dispose()
    env = UnityPy.load(global_data)
    try:
        build = env.file.objects[11].read_typetree(); mono_contract(build)
    finally:
        env.file.reader.dispose()
    return quality, tree, build


def guard_tests(raw, candidate, quality, tree, build):
    passed = []
    def reject(name, action, message):
        try:
            action()
        except ValueError as error:
            require(message in str(error), 'Unexpected negative rejection: ' + name + ': ' + str(error))
            passed.append(name); return
        raise AssertionError('Invalid input accepted: ' + name)
    def replace(data, at, value):
        return data[:at] + value + data[at + len(value):]
    reject('wrong_preimage', lambda: patch_dll(raw[:-1]), 'preimage')
    reject('double_patch', lambda: patch_dll(candidate), 'preimage')
    reject('wrong_grid', lambda: quality_contract(replace(quality, 68, struct.pack('<i', 32))), 'Fixed grid')
    reject('temporal_enabled', lambda: quality_contract(replace(quality, 172, struct.pack('<i', 1))), 'Temporal')
    reject('occlusion_enabled', lambda: quality_contract(replace(quality, 180, struct.pack('<i', 1))), 'Occlusion')
    reject('denoising_enabled', lambda: quality_contract(replace(quality, 148, struct.pack('<i', 1))), 'Denoising')
    reject('blur_enabled', lambda: quality_contract(replace(quality, 156, struct.pack('<i', 1))), 'Blur')
    reject('vr_enabled', lambda: mono_contract({**build, 'enabledVRDevices': ['OpenVR']}), 'non-VR')
    reject('different_graphics_api', lambda: mono_contract({**build, 'm_GraphicsAPIs': [21]}), 'non-VR')
    reject('wrong_max_stack', lambda: inspect_before(replace(raw, 0x4ca2, b'\x04\0')), 'body header')
    reject('wrong_target_anchor', lambda: inspect_before(replace(raw, OFFSET, b'\0')), 'anchor')
    reject('wrong_producer_anchor', lambda: inspect_before(replace(raw, SITES[0][1], b'\0')), 'anchor')
    reject('wrong_accumulation_anchor', lambda: inspect_before(replace(raw, SITES[2][1], b'\0')), 'anchor')
    reject('wrong_existing_direct_call', lambda: inspect_before(replace(raw, 0x4cac + 0x103c, b'\xc9')), 'direct call')
    reject('unrelated_change', lambda: verify_dll(raw, candidate[:-1] + bytes([candidate[-1] ^ 1])), 'inverse')
    bad_tree = copy.deepcopy(tree)
    next(v for v in bad_tree['variants'] if v['targetRenderer'] == 11)['kernels'][0]['variantMap'][0][1]['threadGroupSize'][2] = 1
    reject('wrong_shader_group', lambda: kernel_contract(bad_tree), 'thread-group')
    return passed


def closed_direct_projection(source):
    """Project only the three statically false branches; retain every other call."""
    def block_end(text, opening):
        depth = 1; end = opening + 1
        while depth:
            depth += (text[end] == '{') - (text[end] == '}'); end += 1
        return end
    flag = re.search(r'bool (\w+) = _frustumSettingsToId\.HasFlags\(FrustumParameters\.EnableOcclusionCulling\);', source)
    require(flag is not None, 'Occlusion flag assignment differs')
    needle = 'if (' + flag[1] + ')'
    require(source.count(needle) == 1, 'Occlusion branch is ambiguous')
    at = source.index(needle); opening = source.index('{', at); end = block_end(source, opening)
    require(source[opening:end].count('.DispatchIndirect(') == 3, 'Expected three dead occlusion indirect calls')
    rest = source[end:]; match = re.match(r'\s*else\s*\{', rest)
    require(match is not None, 'No-occlusion else branch differs')
    other_open = end + match.end() - 1; other_end = block_end(source, other_open)
    source = source[:at] + source[other_open:other_end] + source[other_end:]
    for name in ('EnableDenoisingFilter', 'EnableBlurFilter'):
        needle = 'if (_frustumSettingsToId.HasFlags(FrustumParameters.' + name + '))'
        require(source.count(needle) == 1, 'Filter branch differs: ' + name)
        at = source.index(needle); opening = source.index('{', at); end = block_end(source, opening)
        require(source[opening:end].count('.DispatchIndirect(') == 1, 'Expected one dead filter indirect call')
        source = source[:at] + '// ' + name + ' is false in the pinned quality.\n' + source[end:]
    require('.DispatchIndirect(' not in source, 'A live indirect call remains in closed DIRECT mode')
    for shader, groups in (('_computeVisibleCellsComputeShader', '8, 5, 1'),
                           ('_computeDataComputeShader', '8, 5, 32'), ('_computeAccumulationComputeShader', '8, 5, 1')):
        require(re.search(re.escape(shader) + r'\.Dispatch\([^\n]*, ' + groups + r'\);', source),
                'Expected live direct call missing: ' + shader)
    return source


def decompile(path, il=False):
    args = [str(ILSPY), '--disable-updatecheck', '-r', str(SOURCE.parent)]
    args += ['-il'] if il else ['-m', '0x06000092']
    result = subprocess.run(args + [str(path)], capture_output=True, text=True, timeout=30)
    require(result.returncode == 0, 'Installed ILSpy failed')
    if not il:
        return result.stdout
    end = result.stdout.index('// end of method Frustum::ComputeData')
    return result.stdout[result.stdout.rfind('\n\t.method', 0, end):end]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=WORK / 'complete')
    args = parser.parse_args()
    source = args.source.resolve(); output = args.output.resolve()
    require(source in (SOURCE.resolve(), RETAINED.resolve(), COUNTER_SOURCE.resolve()), 'Use canonical or the pinned no-counter source')
    require(output == WORK or output.is_relative_to(WORK), 'Output must remain in work/aura_direct_dispatch29')
    require(not output.exists(), 'Output must be fresh')
    for path in [output, *output.parents]:
        if path == ROOT.parent: break
        require(not path.exists() or not path.is_junction() and not path.is_symlink(), 'Output parent is a link')
    raw = source.read_bytes(); resources = RESOURCES.read_bytes(); level = LEVEL.read_bytes(); global_data = GLOBAL.read_bytes()
    quality, tree, build = asset_boundary(resources, level, global_data)
    candidate, proof = patch_dll(raw)
    negative = guard_tests(raw, candidate, quality, tree, build)
    before_il = decompile(source, True)
    require('IL_1628: callvirt instance void [UnityEngine.CoreModule]UnityEngine.ComputeShader::DispatchIndirect(int32, class [UnityEngine.CoreModule]UnityEngine.ComputeBuffer)' in before_il,
            'Source IL call signature differs')
    require('IL_103b: callvirt instance void [UnityEngine.CoreModule]UnityEngine.ComputeShader::Dispatch(int32, int32, int32, int32)' in before_il,
            'Existing direct method signature differs')
    branches = re.findall(r'IL_[0-9a-f]+: (?:b\w*|leave\w*|switch)[^\n]*', before_il)
    for name, offset, il, old, new, prefix, groups in SITES:
        require(not any(any('IL_' + format(n, '04x') in line for n in range(il, il + 13)) for line in branches),
                'Branch enters replaced argument span: ' + name)
    groups = (64 // 8, 40 // 8, 64 // 2)
    require(groups == (8, 5, 32), 'CPU group calculation differs')
    ids = {x + 64 * (y + 40 * z) for z in range(groups[2] * 2) for y in range(groups[1] * 8) for x in range(groups[0] * 8)}
    require(ids == set(range(163840)), 'Fixed direct launch coverage differs')
    columns = {x + 64 * y for y in range(5 * 8) for x in range(8 * 8)}
    require(columns == set(range(2560)), 'Producer/accumulation column coverage differs')
    output.mkdir(parents=True); target = output / 'Aura2_Core.dll'; target.write_bytes(candidate)
    after_il = decompile(target, True); after_cs = decompile(target)
    for name, offset, il, old, new, prefix, groups in SITES:
        require('IL_' + format(il + 8, '04x') + ': callvirt instance void [UnityEngine.CoreModule]UnityEngine.ComputeShader::Dispatch(int32, int32, int32, int32)' in after_il,
                'Candidate direct call readback differs: ' + name)
    active = closed_direct_projection(after_cs)
    require(re.findall(r'IL_[0-9a-f]+: (?:b\w*|leave\w*|switch)[^\n]*', after_il) == branches, 'Branch offsets changed')
    require(after_il.count('ComputeShader::DispatchIndirect(') == 5 and before_il.count('ComputeShader::DispatchIndirect(') == 8, 'Other indirect calls changed')
    require(sha(source.read_bytes()) == DLL_SHA and sha(RESOURCES.read_bytes()) == RESOURCE_SHA and
            sha(LEVEL.read_bytes()) == LEVEL_SHA and sha(GLOBAL.read_bytes()) == GLOBAL_SHA, 'Inputs changed concurrently')
    (output / 'ComputeData-before.il').write_text(before_il, encoding='utf-8')
    (output / 'ComputeData-after.il').write_text(after_il, encoding='utf-8')
    (output / 'ComputeData-after.cs').write_text(after_cs, encoding='utf-8')
    (output / 'ComputeData-closed-direct.cs').write_text('// Analysis projection: occlusion, denoising and blur branches are statically false.\n' + active, encoding='utf-8')
    manifest = {'source': str(source), 'sourceSha256': DLL_SHA, 'outputSha256': sha(candidate),
                'resourcesSha256': RESOURCE_SHA, 'levelSha256': LEVEL_SHA, 'globalGameManagersSha256': GLOBAL_SHA,
                'qualityRawSha256': QUALITY_SHA, 'lightingRawSha256': LIGHTING_SHA, 'patch': proof,
                'negativeGuards': negative, 'lightingKernelsVerified': 12, 'producerKernelsVerified': [0], 'accumulationKernelsVerified': 8,
                'coveredLightingCells': len(ids), 'coveredProducerAndAccumulationColumns': len(columns), 'branchOffsetsUnchanged': True,
                'runtimeContract': 'Mono 64x40x64 only, DIRECT no-counter, temporal/occlusion/denoising/blur off; no runtime grid changes or XR activation.',
                'remainingIndirectCallsInComputeData': after_il.count('ComputeShader::DispatchIndirect('),
                'remainingLiveIndirectCallsInClosedMode': 0, 'deadIndirectCalls': {'occlusion': 3, 'denoising': 1, 'blur': 1},
                'existingConditionalLightProbeDirectCallUnchanged': True, 'dispatchSizeBufferStillBound': True, 'deviceVerified': False}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print('AURA_THREE_DIRECT_DISPATCH_PASS', target, sha(candidate))


if __name__ == '__main__':
    main()
