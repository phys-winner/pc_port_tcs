"""Prepare an isolated full-grid Aura comparison, without applying any files.

Reuse the guarded typed-coordinate stage, replace only producer kernel0's
atomic index allocation, and disable the scene quality's occlusion flag.
The existing no-occlusion branch supplies count and dispatch sizes from CPU.
"""
import argparse
import copy
import difflib
import json
import re
import struct
import subprocess
from pathlib import Path

import UnityPy
import prepare_mobile_aura_integer_cells as typed
from prepare_depth_precision import container_layout

ROOT = typed.ROOT
WORK = ROOT / 'work/aura_direct23'
LEVEL_SOURCE = ROOT / 'app/obb/assets/bin/Data/level1'
LEVEL_RETAINED = ROOT / 'work/aura_direct_input/level1'
LEVEL_BEFORE = 'd5f3f842dd5504f6cc7c7d6ddaf76fa4585dbc65883d0b2118d76633f0ba693a'
QUALITY_BEFORE = '696ac2b39bd9097eb77703d3576b83a8de0b212e518728a349c1997e5b1c6747'
TYPED_BEFORE = 'ead895801c92844e14cf782e3f0ac8882e7917a81596ee751dabf19449d457b9'
AURA = ROOT / 'app/assets/bin/Data/Managed/Aura2_Core.dll'
AURA_BEFORE = '7c0c666f5eabce1c46be3c476ce8f1c7e4dd2f1774af8287f39feb67ea22aa50'
ILSPY = Path.home() / '.dotnet/tools/ilspycmd.exe'
OLD = '        u_xlatu3 = atomicCounterIncrement(appendedCellsBuffer_counter);'
NEW = ('        u_xlatu3 = portCell.x + uint(Aura_BufferResolution.x) * '
       '(portCell.y + uint(Aura_BufferResolution.y) * portCell.z);')
require, sha = typed.require, typed.sha


def direct_code(source):
    require(source.count(OLD) == 1, 'Exact atomic index anchor required')
    require('uvec3 portCell = uvec3(gl_GlobalInvocationID.xy, 0u);' in source,
            'Typed-coordinate producer required')
    require('occlusionTexture' not in source, 'Expected no-occlusion kernel0')
    result = source.replace(OLD, NEW)
    require(result.replace(NEW, OLD) == source, 'Shader inverse differs')
    stores = lambda s: re.findall(r'^\s*appendedCellsBuffer_buf\[.*$', s, re.M)
    require(len(stores(source)) == 10 and stores(source) == stores(result), 'Ten-word payload changed')
    require(result.count('atomicCounterIncrement') == 0, 'GPU append atomic remains')
    return result


def direct_tree(tree):
    target = copy.deepcopy(tree)
    case = typed.cases(target)[0]
    old = bytes(case['code'])
    require(old.endswith(b'\0'), 'Shader terminator differs')
    source = old[:-1].decode('utf-8')
    result = direct_code(source)
    case['code'] = list(result.encode('utf-8') + b'\0')
    inverse = copy.deepcopy(target)
    typed.cases(inverse)[0]['code'] = typed.cases(tree)[0]['code']
    require(inverse == tree, 'Kernel0 metadata or another kernel changed')
    return target, source, result


def prepare_resources(raw):
    stage, _ = typed.prepare(raw)
    require(sha(stage) == TYPED_BEFORE, 'Typed stage preimage differs')
    typed.verify(raw, stage)
    env = UnityPy.load(stage)
    try:
        obj = env.file.objects[1825]
        target, source, result = direct_tree(obj.read_typetree())
        obj.save_typetree(target)
        return stage, env.file.save(), source, result
    finally:
        env.file.reader.dispose()


def verify_resources(original_bytes, stage_bytes, direct_bytes):
    require(sha(original_bytes) == typed.BEFORE and sha(stage_bytes) == TYPED_BEFORE,
            'Exact resource stages required')
    original, stage, direct = (UnityPy.load(b) for b in (original_bytes, stage_bytes, direct_bytes))
    try:
        require(container_layout(original.file) == container_layout(stage.file) == container_layout(direct.file),
                'Resources container metadata changed')
        expected, _, _ = direct_tree(stage.file.objects[1825].read_typetree())
        require(direct.file.objects[1825].read_typetree() == expected, 'Direct shader differs from exact patch')
        for pid, obj in original.file.objects.items():
            if pid != 1825:
                require(stage.file.objects[pid].get_raw_data() == direct.file.objects[pid].get_raw_data() ==
                        obj.get_raw_data(), 'Unrelated resources raw object changed: ' + str(pid))
        obj = direct.file.objects[1825]
        result_hash = sha(obj.get_raw_data())
        obj.set_raw_data(stage.file.objects[1825].get_raw_data())
        require(direct.file.save() == stage_bytes, 'Direct-to-typed full container inverse differs')
        obj.set_raw_data(original.file.objects[1825].get_raw_data())
        require(direct.file.save() == original_bytes, 'Direct-to-original full container inverse differs')
        return {'otherRawObjectsPreserved': len(original.file.objects) - 1,
                'object1825AfterSha256': result_hash, 'directToTypedInverseExact': True,
                'directToOriginalInverseExact': True, 'kernel0OnlyBeyondTypedStage': True,
                'allTenPayloadStoresExact': True, 'evaluatorUnchanged': True}
    finally:
        for env in (original, stage, direct):
            env.file.reader.dispose()


def quality_contract(raw):
    require(len(raw) == 196 and raw[32:61] == b'Default Aura Quality Settings', 'Quality identity differs')
    require(struct.unpack_from('<3i', raw, 68) == (64, 40, 64), 'Expected authored64x40x64 grid')
    require(struct.unpack_from('<i', raw, 172)[0] == 0, 'Temporal must remain disabled')
    require(struct.unpack_from('<i', raw, 180)[0] == 1, 'Occlusion must initially be enabled')
    require(all(struct.unpack_from('<i', raw, i)[0] == 0 for i in (148, 156, 184, 192)),
            'Unexpected denoise/blur/debug/light-probes flags')


def prepare_level(raw):
    require(sha(raw) == LEVEL_BEFORE, 'Exact level1 preimage required')
    env = UnityPy.load(raw)
    try:
        obj = env.file.objects[36456]
        require(obj.type.name == 'MonoBehaviour' and sha(obj.get_raw_data()) == QUALITY_BEFORE,
                'Exact quality object required')
        quality_contract(obj.get_raw_data())
        offset = obj.byte_start + 180
        require(raw[offset] == 1 and raw[offset + 1:offset + 4] == b'\0\0\0', 'Flag encoding differs')
        return raw[:offset] + b'\0' + raw[offset + 1:], offset
    finally:
        env.file.reader.dispose()


def verify_level(before, after, offset):
    require(sha(before) == LEVEL_BEFORE, 'Exact level1 preimage required')
    require(len(before) == len(after) and before[offset] == 1 and after[offset] == 0,
            'Expected one-byte occlusion transition')
    require(after[:offset] + b'\1' + after[offset + 1:] == before, 'Full level1 inverse differs')
    original, candidate = UnityPy.load(before), UnityPy.load(after)
    try:
        require(container_layout(original.file) == container_layout(candidate.file), 'Level metadata changed')
        obj = original.file.objects[36456]
        require(offset == obj.byte_start + 180, 'Quality byte offset differs')
        quality_contract(obj.get_raw_data())
        for pid, item in original.file.objects.items():
            old, new = item.get_raw_data(), candidate.file.objects[pid].get_raw_data()
            if pid == 36456:
                require(new == old[:180] + b'\0' + old[181:], 'Unrelated quality field changed')
            else:
                require(old == new, 'Unrelated level raw object changed: ' + str(pid))
        return {'otherRawObjectsPreserved': len(original.file.objects) - 1,
                'pathId': 36456, 'relativeOffset': 180, 'absoluteOffset': offset,
                'qualityBeforeSha256': QUALITY_BEFORE,
                'qualityAfterSha256': sha(candidate.file.objects[36456].get_raw_data()),
                'changedBytes': 1, 'fullContainerInverseExact': True, 'temporalRemainsDisabled': True}
    finally:
        original.file.reader.dispose()
        candidate.file.reader.dispose()


def caller_proof(resource_bytes):
    require(sha(AURA.read_bytes()) == AURA_BEFORE and ILSPY.is_file(), 'Pinned Aura source/tool required')
    texts = {}
    for name, token in (('ComputeData', '0x06000092'), ('ComputeDispatchSizes', '0x06000096')):
        result = subprocess.run([str(ILSPY), '--disable-updatecheck', '-m', token, str(AURA)],
                                capture_output=True, text=True, timeout=30)
        require(result.returncode == 0 and name + '(' in result.stdout, 'Caller decompile failed')
        texts[name] = result.stdout
    sizes, compute = texts['ComputeDispatchSizes'], texts['ComputeData']
    for anchor in ('frustumGridResolution.x / (int)_threadSizeX',
                   'frustumGridResolution.y / (int)_threadSizeY',
                   'frustumGridResolution.z / (int)_threadSizeZ',
                   '_dispatchBuffers[1].SetData(array);', '_dispatchBuffers[3].SetData(array);',
                   'frustumGridResolution.x * frustumGridResolution.y * frustumGridResolution.z',
                   '_dispatchBuffers[0].SetData(array);'):
        require(sizes.count(anchor) == 1, 'CPU dispatch source anchor differs: ' + anchor)
    start = compute.index('if (_previousOcclusionCullingState)')
    end = compute.index('DataVolumeTexture.Swap();', start)
    branch = compute[start:end]
    for anchor in ('ComputeDispatchSizes();', 'int kernelIndex2 = (flag ? 1 : 0);',
                   'Aura.ResourcesCollection.DummyTextureUAV',
                   '_computeVisibleCellsComputeShader.DispatchIndirect(kernelIndex2, _dispatchBuffers[3]);'):
        require(branch.count(anchor) == 1, 'No-occlusion source anchor differs: ' + anchor)
    require('CopyCount' not in branch and '"sizeBuffer"' not in branch, 'No-occlusion writes GPU commands')
    require('_dispatchBuffers[5].SetCounterValue(0u);' in compute, 'Expected retained counter reset')
    env = UnityPy.load(resource_bytes)
    try:
        tree = env.file.objects[1826].read_typetree()
        kernels = next(v for v in tree['variants'] if v['targetRenderer'] == 11)['kernels']
        require(len(kernels) == 12, 'Lighting kernel count differs')
        for k in kernels:
            case = k['variantMap'][0][1]
            code = bytes(case['code']).decode()
            require(case['threadGroupSize'] == [8, 8, 2] and
                    code.count('.xy << ivec2(3, 3);') == 1 and
                    code.count(' * ivec2(gl_GlobalInvocationID.zy);') == 1 and
                    'dispatchSizeBuffer_buf[0].value[(0 >> 2) + 0]' in code and
                    'dispatchSizeBuffer_buf[1].value[(0 >> 2) + 0]' in code,
                    'Consumer flattening contract differs')
    finally:
        env.file.reader.dispose()
    require(sha(AURA.read_bytes()) == AURA_BEFORE, 'Aura DLL changed concurrently')
    return texts, {'auraDllSha256': AURA_BEFORE, 'lightingKernelsChecked': 12,
                   'cpuCount': 163840, 'cpuLightingGroups': [8, 5, 32], 'producerGroups': [8, 5, 1],
                   'gpuAtomicRemoved': True, 'copyCountAndEvaluatorSkipped': True,
                   'nativeCounterResetRemains': True, 'dispatchIndirectRemains': True}


def coverage():
    grids = []
    for x, y, z in ((64, 40, 64), (8, 8, 2), (24, 16, 10)):
        count = x * y * z
        producer = [ix + x * (iy + y * iz) for iy in range(y) for ix in range(x) for iz in range(z)]
        gx, gy, gz = x // 8, y // 8, z // 2
        consumer = [ix + gx * 8 * (iy + gy * 8 * iz)
                    for iz in range(gz * 2) for iy in range(gy * 8) for ix in range(gx * 8)]
        require(sorted(producer) == consumer == list(range(count)), 'Producer/consumer coverage differs')
        grids.append({'grid': [x, y, z], 'cells': count, 'maxIndex': max(producer),
                      'lightingGroups': [gx, gy, gz], 'uniqueAndComplete': True})
    direct = [None] * 163840
    for y in range(40):
        for x in range(64):
            records, last = typed.run_column(x, y, 64, False, None, 0, None)
            require(last == 63 and len(records) == 64, 'Typed source column differs')
            for z, record in enumerate(records):
                index = x + 64 * (y + 40 * z)
                require(direct[index] is None, 'Duplicate direct-cell writer')
                direct[index] = record
    for index, record in enumerate(direct):
        z, remainder = divmod(index, 64 * 40)
        y, x = divmod(remainder, 64)
        expected, _ = typed.payload((x, y, z), False, 0, None)
        require(record == expected and len(record) == 40, 'Full payload permutation differs')
    return {'status': 'PASS', 'grids': grids, 'completePayloadsCompared': len(direct),
            'wordsPerPayload': 10, 'payloadBytes': 40, 'floatCalculationsAndStoresUnchanged': True}


def guard_tests(resources, stage, direct, source, level, changed_level, offset):
    passed = []

    def reject(name, action, text):
        try:
            action()
        except ValueError as error:
            require(text in str(error), 'Wrong guard rejection: ' + name + ': ' + str(error))
            passed.append(name)
            return
        raise AssertionError('Invalid candidate accepted: ' + name)

    reject('wrong_resources_preimage', lambda: prepare_resources(resources[:-1]), 'preimage')
    reject('missing_atomic_anchor', lambda: direct_code(source.replace(OLD, '')), 'atomic index anchor')
    reject('duplicate_atomic_anchor', lambda: direct_code(source + '\n' + OLD), 'atomic index anchor')
    reject('already_direct_shader', lambda: direct_code(direct_code(source)), 'atomic index anchor')
    reject('wrong_level_preimage', lambda: prepare_level(level[:-1]), 'preimage')
    reject('already_disabled_quality', lambda: prepare_level(changed_level), 'preimage')
    env = UnityPy.load(level)
    try:
        quality = env.file.objects[36456].get_raw_data()
    finally:
        env.file.reader.dispose()
    reject('temporal_enabled', lambda: quality_contract(quality[:172] + b'\1' + quality[173:]), 'Temporal')
    bad = changed_level[:offset - 8] + bytes([changed_level[offset - 8] ^ 1]) + changed_level[offset - 7:]
    reject('unrelated_quality_byte', lambda: verify_level(level, bad, offset), 'inverse')

    def mutated(change):
        env = UnityPy.load(direct)
        try:
            change(env.file)
            return env.file.save()
        finally:
            env.file.reader.dispose()

    def alter_code(file, kernel, old, new):
        obj = file.objects[1825]
        tree = obj.read_typetree()
        case = typed.cases(tree)[kernel]
        code = bytes(case['code'])
        require(code.count(old) == 1, 'Negative fixture anchor differs')
        case['code'] = list(code.replace(old, new))
        obj.save_typetree(tree)

    for name, kernel, old, new in (
        ('changed_index_stride', 0, NEW.encode(), NEW.replace('Resolution.y', 'Resolution.x').encode()),
        ('changed_payload_word', 0, b'floatBitsToUint(u_xlat2.w)', b'floatBitsToUint(u_xlat0.w)'),
        ('changed_evaluator', 4, b' * 4u;', b' * 8u;')):
        bad = mutated(lambda file, k=kernel, a=old, b=new: alter_code(file, k, a, b))
        reject(name, lambda b=bad: verify_resources(resources, stage, b), 'exact patch')

    def alter_other(file):
        obj = file.objects[1826]
        raw = obj.get_raw_data()
        obj.set_raw_data(raw[:-1] + bytes([raw[-1] ^ 1]))

    bad = mutated(alter_other)
    reject('changed_lighting_raw', lambda: verify_resources(resources, stage, bad), 'Unrelated resources')
    return passed


def source_path(value, allowed):
    path = value.resolve()
    require(path in allowed and not value.is_symlink(), 'Source path is not one of the pinned inputs')
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--resources-source', type=Path, default=typed.RETAINED_SOURCE)
    parser.add_argument('--level-source', type=Path, default=LEVEL_SOURCE)
    parser.add_argument('--output', type=Path, default=WORK)
    args = parser.parse_args()
    resource_path = source_path(args.resources_source, (typed.SOURCE, typed.RETAINED_SOURCE))
    level_path = source_path(args.level_source, (LEVEL_SOURCE, LEVEL_RETAINED))
    output = args.output.resolve()
    require(output == WORK or output.is_relative_to(WORK), 'Output must stay in work/aura_direct23')
    require(not args.output.is_symlink() and not output.exists(), 'Output must be a fresh ordinary directory')
    resources, level = resource_path.read_bytes(), level_path.read_bytes()
    stage, direct, source, code = prepare_resources(resources)
    changed_level, offset = prepare_level(level)
    resource_receipt = verify_resources(resources, stage, direct)
    level_receipt = verify_level(level, changed_level, offset)
    callers, caller_receipt = caller_proof(resources)
    proof = coverage()
    guards = guard_tests(resources, stage, direct, source, level, changed_level, offset)
    require(sha(resource_path.read_bytes()) == typed.BEFORE and sha(level_path.read_bytes()) == LEVEL_BEFORE,
            'Input changed concurrently')
    output.mkdir(parents=True)
    path = output / 'kernel0-direct.comp'
    path.write_text(code, encoding='utf-8', newline='\n')
    (output / 'kernel0-typed.comp').write_text(source, encoding='utf-8', newline='\n')
    result = subprocess.run([str(typed.COMPILER), '--target-env=opengl', '-fshader-stage=compute',
                             '-fauto-map-locations', '-fauto-bind-uniforms', str(path),
                             '-o', str(path.with_suffix('.spv'))], capture_output=True, text=True, timeout=30)
    require(result.returncode == 0, 'Direct GLSL compile failed: ' + result.stderr)
    for name, raw in (('resources.assets', direct), ('level1', changed_level)):
        with (output / name).open('xb') as stream:
            stream.write(raw)
    verify_resources(resources, stage, (output / 'resources.assets').read_bytes())
    verify_level(level, (output / 'level1').read_bytes(), offset)
    require(sha(resource_path.read_bytes()) == typed.BEFORE and sha(level_path.read_bytes()) == LEVEL_BEFORE,
            'Input changed concurrently')
    (output / 'kernel0.diff').write_text(''.join(difflib.unified_diff(
        source.splitlines(True), code.splitlines(True), fromfile='kernel0-typed.comp',
        tofile='kernel0-direct.comp')), encoding='utf-8')
    for name, text in callers.items():
        (output / (name + '.cs')).write_text(text, encoding='utf-8')
    receipt = {'format': 'closing-shift-aura-direct-cells-v1', 'status': 'PREPARED_ONLY',
               'generatorSha256': sha(Path(__file__).read_bytes()),
               'typedGeneratorSha256': sha(Path(typed.__file__).read_bytes()),
               'resourcesSource': str(resource_path), 'resourcesBeforeSha256': typed.BEFORE,
               'typedStageSha256': TYPED_BEFORE, 'resourcesAfterSha256': sha(direct),
               'levelSource': str(level_path), 'levelBeforeSha256': LEVEL_BEFORE,
               'levelAfterSha256': sha(changed_level), 'resources': resource_receipt, 'level': level_receipt,
               'callerProof': caller_receipt, 'coverage': proof, 'negativeGuards': guards,
               'compile': {'compiler': str(typed.COMPILER), 'exitCode': result.returncode,
                           'stdout': result.stdout, 'stderr': result.stderr},
               'canonicalOrDeviceModified': False, 'deviceAcceptance': False}
    for name, value in (('manifest.json', receipt), ('coverage.json', proof)):
        with (output / name).open('x', encoding='utf-8') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')
    print(json.dumps({'status': receipt['status'], 'output': str(output),
                      'resourcesSha256': sha(direct), 'levelSha256': sha(changed_level),
                      'generatorSha256': receipt['generatorSha256']}, indent=2))


if __name__ == '__main__':
    main()
