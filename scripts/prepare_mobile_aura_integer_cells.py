"""Prepare a private Aura producer fix; never modifies the canonical app.

The generated GLES producer stores integer cell coordinates in float bits.
GLSL ES permits flushing subnormal values, including all positive coordinates
in this grid. Keep these coordinates and the last visited slice as integers.
Only renderer 11 / ComputeShader 1825 / producer kernels 0..3 may change.
"""
import argparse
import copy
import difflib
import hashlib
import json
import math
import os
import re
import struct
import subprocess
from pathlib import Path

import UnityPy
from prepare_depth_precision import container_layout

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'app/obb/assets/bin/Data/resources.assets'
RETAINED_SOURCE = ROOT / 'work/aura_integer_input/resources.assets'
WORK = ROOT / 'work/aura_integer22'
COMPILER = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/ndk/27.2.12479018/shader-tools/windows-x86_64/glslc.exe'
BEFORE = '14605487e73ea8e27716ea7dfb3c130492ca9c1346a7f4e41938e50247f4413c'
OBJECT_BEFORE = 'ced8494b63866184ee9417fb9c1fa64fe884690e405165777da5f1404c55c790'
CODE_BEFORE = (
    '95a60034f5ed46e483560129b0ec4ed20f01b3ef6fc06ed7b264aee6bdf40d0e',
    '1cb06e2a822d1f23d2a2da552a881583639bd6eeb5b7672d17b8e2efc2f0ed06',
    '7c9208b42dd16abc9547cfff3ee0345bbbe31d2c02d1164c7ba35a08cd972a6f',
    '8cb3fca8911187c617357f87601e9b89bdadc01d8dd41425351882950f1c9d2d',
)


def require(ok, message):
    if not ok:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def cases(tree):
    variants = [v for v in tree['variants'] if v['targetRenderer'] == 11]
    require(len(variants) == 1 and len(variants[0]['kernels']) == 5, 'Unexpected GLES variants')
    kernels = variants[0]['kernels']
    require(all(len(k['variantMap']) == 1 for k in kernels), 'Unexpected keyword variants')
    require([k['name'] for k in kernels] == ['ComputeVisibleCells'] * 4 + ['EvaluateDispatchSize'],
            'Unexpected kernel identity/order')
    return [k['variantMap'][0][1] for k in kernels]


def patch_text(source, index):
    """Exact anchors are also exercised independently of the preimage hashes."""
    coord, last, increment = (
        ('u_xlat2', 'u_xlat5', 'u_xlat4'),
        ('u_xlat2', 'u_xlat14', 'u_xlat21'),
        ('u_xlat2', 'u_xlat8', 'u_xlat12'),
        ('u_xlat1', 'u_xlat21', 'u_xlat16'),
    )[index]
    result = source
    edits = []

    def change(old, new, count=1):
        nonlocal result
        require(result.count(old) == count, 'Anchor count differs: ' + repr(old))
        result = result.replace(old, new)
        edits.append((old, new, count))

    require('portCell' not in source and 'portLastSlice' not in source, 'Already patched source')
    change(f'    {coord}.xy = uintBitsToFloat(gl_GlobalInvocationID.xy);',
           '    uvec3 portCell = uvec3(gl_GlobalInvocationID.xy, 0u);')
    change(f'    {last} = 0.0;', '    uint portLastSlice = 0u;')
    change(f'    {coord}.z = 0.0;\n', '')
    for axis in 'xyz':
        count = (4 if index in (1, 3) else 3) if axis == 'z' else 1
        change(f'uint(floatBitsToUint({coord}.{axis}))', f'portCell.{axis}', count)
    if index in (1, 3):
        change(f'floatBitsToInt({coord}.xyz)', 'ivec3(portCell)')
    change(f'        {increment} = intBitsToFloat(floatBitsToInt({coord}.z) + 1);\n', '')
    change(f'{last} = {coord}.z;', 'portLastSlice = portCell.z;', 2 if index >= 2 else 1)
    change(f'        {coord}.z = {increment};', '        portCell.z++;')
    change(f'floatBitsToInt(vec4({last}))', 'ivec4(int(portLastSlice))')

    # Compare all ten append statements; only the three integer coordinates differ.
    stores = lambda s: re.findall(r'^\s*appendedCellsBuffer_buf\[.*$', s, re.M)
    old_stores, new_stores = stores(source), stores(result)
    require(len(old_stores) == len(new_stores) == 10, 'Expected complete ten-word payload')
    require(old_stores[3:] == new_stores[3:], 'Floating payload words changed')
    for i, axis in enumerate('xyz'):
        require(new_stores[i] == old_stores[i].replace(
            f'uint(floatBitsToUint({coord}.{axis}))', f'portCell.{axis}'), 'Coordinate payload differs')
    tail = result[result.index('    uvec3 portCell'):]
    require(not re.search(r'\b' + coord + r'\.[xyz]', tail), 'Float coordinate alias remains')
    require('intBitsToFloat' not in tail, 'Float bitcast increment remains')
    require(result.count('atomicCounterIncrement(') == source.count('atomicCounterIncrement(') == 1,
            'Append count changed')
    # Replacement fragments containing removed text cannot be inverted by replace;
    # a unified diff and a complete object inverse below retain the exact preimage.
    return result, edits


def patch_tree(tree):
    target = copy.deepcopy(tree)
    old_cases, new_cases = cases(tree), cases(target)
    rows = []
    for index in range(4):
        raw = bytes(old_cases[index]['code'])
        require(sha(raw) == CODE_BEFORE[index], 'Exact kernel preimage required: ' + str(index))
        require(raw.endswith(b'\0') and not raw.endswith(b'\0\0'), 'Unexpected shader terminator')
        require(old_cases[index]['threadGroupSize'] == [8, 8, 1], 'Producer group size differs')
        source = raw[:-1].decode('utf-8')
        text, edits = patch_text(source, index)
        code = text.encode('utf-8') + b'\0'
        new_cases[index]['code'] = list(code)
        rows.append({'kernel': index, 'beforeSha256': sha(raw), 'afterSha256': sha(code),
                     'source': source, 'target': text, 'edits': edits})
    reverse = copy.deepcopy(target)
    for index in range(4):
        cases(reverse)[index]['code'] = old_cases[index]['code']
    require(reverse == tree, 'Unrelated shader metadata/variants changed')
    return target, rows


def prepare(raw):
    require(len(raw) == 106193728 and sha(raw) == BEFORE, 'Exact resources preimage required')
    env = UnityPy.load(raw)
    try:
        obj = env.file.objects[1825]
        require(obj.type.name == 'ComputeShader' and sha(obj.get_raw_data()) == OBJECT_BEFORE,
                'Exact ComputeShader 1825 preimage required')
        tree = obj.read_typetree()
        require(tree['m_Name'] == 'ComputeVisibleCellsBufferComputeShader', 'Shader name differs')
        target, rows = patch_tree(tree)
        obj.save_typetree(target)
        return env.file.save(), rows
    finally:
        env.file.reader.dispose()


def verify(before, after):
    require(sha(before) == BEFORE, 'Exact resources preimage required')
    original, candidate = UnityPy.load(before), UnityPy.load(after)
    try:
        require(container_layout(original.file) == container_layout(candidate.file),
                'Container identity/platform/externals/object metadata changed')
        old_obj, new_obj = original.file.objects[1825], candidate.file.objects[1825]
        expected, rows = patch_tree(old_obj.read_typetree())
        require(new_obj.read_typetree() == expected, 'Candidate shader differs from exact patch')
        for pid, obj in original.file.objects.items():
            if pid != 1825:
                require(candidate.file.objects[pid].get_raw_data() == obj.get_raw_data(),
                        'Unrelated raw object changed: ' + str(pid))
        new_raw = new_obj.get_raw_data()
        # This also verifies serialized type trees, aliases, offsets/padding after
        # deterministic reserialization, not only the parsed fields we inspect.
        new_obj.set_raw_data(old_obj.get_raw_data())
        require(candidate.file.save() == before, 'Full container inverse differs')
        return {'preservedRawObjects': len(original.file.objects) - 1,
                'objectBeforeSha256': OBJECT_BEFORE, 'objectAfterSha256': sha(new_raw),
                'containerLayoutPreserved': True, 'fullContainerInverseExact': True,
                'evaluatorAndAllOtherShadersExact': True, 'floatingPayloadWordsExact': True,
                'unityVersion': original.file.unity_version,
                'targetPlatform': int(original.file.target_platform)}
    finally:
        original.file.reader.dispose()
        candidate.file.reader.dispose()


def f32(value):
    return struct.unpack('<f', struct.pack('<f', value))[0]


def as_float(word, ftz=False):
    if ftz and 0 < (word & 0x7fffffff) < 0x00800000:
        return 0.0
    return struct.unpack('<f', struct.pack('<I', word & 0xffffffff))[0]


def as_uint(value):
    return struct.unpack('<I', struct.pack('<f', value))[0]


def depth_bias(value, coefficient=2.0):
    value = f32(max(f32(1.0 - value), f32(9.99999997e-07)))
    return f32(1.0 - f32(2.0 ** f32(f32(math.log2(value)) * coefficient)))


def payload(coords, temporal, frame, samples):
    """CPU float32 reference for the unchanged seven floating payload words.

    Transcendentals are a CPU reference, not an assertion about GPU rounding.
    Exact source preservation plus identical integer inputs establishes that
    both shaders perform the same floating operations on any given backend.
    """
    x, y, z = coords
    tx, ty, tz = f32(1 / 64), f32(1 / 40), f32(1 / 64)
    cx, cy, cz = f32((x + .5) * tx), f32((y + .5) * ty), f32((z + .5) * tz)
    biased = depth_bias(cz)
    if temporal:
        base = f32(f32(y * f32(.970000029)) + f32(x * f32(1.23000002)))
        seed = f32(f32(f32((z + frame) & 0xffffffff) * f32(1.00999999)) + base)
        noise = f32(f32(math.sin(f32(seed + 236526.0))) * f32(43758.5469))
        noise = f32(noise - math.floor(noise))
        index = (x + frame + 2 * y + z + int(f32(noise * 64))) & 63
        jx = f32(f32(samples[index][0] * tx) + cx)
        jy = f32(f32(samples[index][1] * ty) + cy)
        jz = f32(f32(f32(noise - .5) * tz) + cz)
        floats = (cx, cy, biased, cz, jx, jy, depth_bias(jz))
    else:
        floats = (cx, cy, biased, cz, cx, cy, cz)
    words = tuple(coords) + tuple(map(as_uint, floats))
    require(len(words) == 10, 'Reference payload length differs')
    return struct.pack('<10I', *words), biased


def run_column(x, y, count, temporal, occlusion, frame, samples, old=False, ftz=False):
    """Independent old bit-representation and integer-counter control models."""
    stored_x, stored_y = as_float(x, ftz), as_float(y, ftz)
    stored_z, last_float = 0.0, 0.0
    integer_z, last_integer = 0, 0
    result = []
    for watchdog in range(max(count + 2, 130)):
        z = as_uint(stored_z) if old else integer_z
        if z >= count:
            return result, as_uint(last_float) if old else last_integer
        coords = (as_uint(stored_x), as_uint(stored_y), z) if old else (x, y, z)
        data, biased = payload(coords, temporal, frame, samples)
        result.append(data)
        if occlusion is not None and occlusion < biased:
            return result, as_uint(stored_z) if old else integer_z
        if old:
            next_float = as_float((as_uint(stored_z) + 1) & 0xffffffff, ftz)
            last_float, stored_z = stored_z, next_float
        else:
            last_integer = integer_z
            integer_z += 1
    raise RuntimeError('MODEL_WATCHDOG: producer did not terminate')


def equivalence(rows):
    matches = re.findall(r'ImmCB_0_0_0\[(\d+)\] = vec2\(([^,]+), ([^)]+)\);', rows[1]['source'])
    require([int(i) for i, _, _ in matches] == list(range(64)), 'Temporal sample table differs')
    samples = [(f32(float(x)), f32(float(y))) for _, x, y in matches]
    require(re.findall(r'ImmCB_0_0_0\[\d+\] = vec2\([^;]+;', rows[1]['source']) ==
            re.findall(r'ImmCB_0_0_0\[\d+\] = vec2\([^;]+;', rows[3]['source']),
            'Temporal sample tables are not shared')
    cells, columns = 0, 0
    # Both temporal paths cover every cell of the actual 64 x 40 x 64 grid.
    # Opaque-depth kernels also run full-length here; all break cases follow.
    for kernel in range(4):
        for y in range(40):
            for x in range(64):
                args = (x, y, 64, bool(kernel & 1), 1.0 if kernel >= 2 else None, 17, samples)
                old = run_column(*args, old=True)
                new = run_column(*args)
                require(old == new and len(new[0]) == 64 and new[1] == 63,
                        'Full ten-word payload or final slice differs')
                cells += len(new[0])
                columns += 1
    boundaries = []
    for temporal in (False, True):
        for frame in (0, 1, 63, 64, 65535, 0x7fffffff):
            for count, expected_last, stop in ((0, 0, None), (1, 0, None), (64, 63, None),
                                               (64, 0, -1.0), (64, 31, depth_bias(31 / 64)),
                                               (64, 63, depth_bias(63 / 64))):
                args = (63, 39, count, temporal, stop, frame, samples)
                old, new = run_column(*args, old=True), run_column(*args)
                require(old == new and new[1] == expected_last, 'Temporal/occlusion boundary differs')
                require(len(new[0]) == (0 if count == 0 else expected_last + 1),
                        'Append-before-occlusion order differs')
                boundaries.append((temporal, frame, count, expected_last, len(new[0])))
    try:
        run_column(63, 39, 64, False, 1.0, 0, samples, old=True, ftz=True)
    except RuntimeError as error:
        require(str(error).startswith('MODEL_WATCHDOG'), 'Unexpected FTZ model failure')
    else:
        raise AssertionError('Old producer unexpectedly terminated under permitted FTZ')
    typed = run_column(63, 39, 64, False, 1.0, 0, samples, ftz=True)
    require(len(typed[0]) == 64 and typed[1] == 63, 'Typed producer failed FTZ control')
    first = struct.unpack('<10I', typed[0][0])
    require(first[:3] == (63, 39, 0), 'Typed X/Y payload was corrupted')
    return {'status': 'PASS', 'fullGridPayloadComparisons': cells, 'columns': columns,
            'payloadBytes': 40, 'wordsComparedPerCell': 10, 'boundaryCases': len(boundaries),
            'boundaryResults': boundaries, 'oldFtzModelWatchdogTriggered': True,
            'typedFtzModelTerminates': True, 'typedFtzCoordinatesPreserved': True,
            'limit': 'CPU reference and exact source invariants; device causality requires hardware acceptance.'}


def guard_tests(raw, candidate, rows):
    passed = []

    def reject(name, action, message):
        try:
            action()
        except ValueError as error:
            require(message in str(error), 'Wrong rejection for ' + name + ': ' + str(error))
            passed.append(name)
            return
        raise AssertionError('Invalid input accepted: ' + name)

    reject('unknown_resources', lambda: prepare(bytes([raw[0] ^ 1]) + raw[1:]), 'preimage')
    reject('already_patched', lambda: prepare(candidate), 'preimage')
    for row in rows:
        index, source = row['kernel'], row['source']
        reject('missing_increment_anchor_' + str(index),
               lambda s=source, i=index: patch_text(s.replace(' + 1);', ' + 2);'), i), 'Anchor count')
        reject('duplicate_coordinate_anchor_' + str(index),
               lambda s=source, i=index: patch_text(s + '\n' + next(
                   line for line in s.splitlines() if ' = uintBitsToFloat(gl_GlobalInvocationID.xy);' in line), i),
               'Anchor count')

    def mutated(change):
        env = UnityPy.load(candidate)
        try:
            change(env.file)
            return env.file.save()
        finally:
            env.file.reader.dispose()

    bad = mutated(lambda file: setattr(file, '_m_target_platform', 19))
    reject('changed_platform', lambda: verify(raw, bad), 'identity/platform')

    def change_code(file, kernel, old, new):
        obj = file.objects[1825]
        tree = obj.read_typetree()
        case = cases(tree)[kernel]
        code = bytes(case['code'])
        require(code.count(old) == 1, 'Negative test anchor differs')
        case['code'] = list(code.replace(old, new))
        obj.save_typetree(tree)

    bad = mutated(lambda file: change_code(file, 2, b'floatBitsToUint(u_xlat2.w)',
                                           b'floatBitsToUint(u_xlat1.w)'))
    reject('changed_floating_payload', lambda: verify(raw, bad), 'exact patch')
    bad = mutated(lambda file: change_code(file, 4, b' * 4u;', b' * 8u;'))
    reject('changed_evaluator', lambda: verify(raw, bad), 'exact patch')

    def change_lighting(file):
        obj = file.objects[1826]
        data = obj.get_raw_data()
        obj.set_raw_data(data[:-1] + bytes([data[-1] ^ 1]))

    bad = mutated(change_lighting)
    reject('changed_lighting_raw_object', lambda: verify(raw, bad), 'Unrelated raw object')
    return passed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE,
                        help='Canonical file or pinned work/aura_integer_input/resources.assets preimage')
    parser.add_argument('--output', type=Path, default=WORK)
    args = parser.parse_args()
    source = args.source.resolve()
    require(source in (SOURCE, RETAINED_SOURCE) and not args.source.is_symlink(),
            'Source must be the canonical file or the retained aura_integer_input preimage')
    output = args.output.resolve()
    require(output == WORK or output.is_relative_to(WORK), 'Output must stay in work/aura_integer22')
    require(not args.output.is_symlink() and not output.exists(), 'Output must be a fresh ordinary directory')
    require(COMPILER.is_file(), 'Installed glslc is missing')
    raw = source.read_bytes()
    candidate, rows = prepare(raw)
    receipt = verify(raw, candidate)
    guards = guard_tests(raw, candidate, rows)
    proof = equivalence(rows)
    require(sha(source.read_bytes()) == BEFORE, 'Source changed concurrently')
    output.mkdir(parents=True)
    compile_rows = []
    for row in rows:
        index = row['kernel']
        for kind in ('source', 'target'):
            path = output / f'kernel{index}-{kind}.comp'
            path.write_text(row[kind], encoding='utf-8', newline='\n')
            result = subprocess.run([str(COMPILER), '--target-env=opengl', '-fshader-stage=compute',
                                     '-fauto-map-locations', '-fauto-bind-uniforms', str(path),
                                     '-o', str(path.with_suffix('.spv'))], capture_output=True, text=True)
            compile_rows.append({'kernel': index, 'kind': kind, 'exitCode': result.returncode,
                                 'stdout': result.stdout, 'stderr': result.stderr})
            require(result.returncode == 0, f'GLSL compile failed kernel{index}-{kind}: {result.stderr}')
        (output / f'kernel{index}.diff').write_text(''.join(difflib.unified_diff(
            row['source'].splitlines(True), row['target'].splitlines(True),
            fromfile=f'kernel{index}-source.comp', tofile=f'kernel{index}-target.comp')), encoding='utf-8')
    path = output / 'resources.assets'
    with path.open('xb') as stream:
        stream.write(candidate)
    verify(raw, path.read_bytes())
    require(sha(source.read_bytes()) == BEFORE, 'Source changed concurrently')
    receipt.update(format='closing-shift-aura-integer-cells-v1', status='PREPARED_ONLY',
                   source=str(source), sourceSha256=BEFORE, candidate=str(path), candidateSha256=sha(candidate),
                   pathId=1825, renderer=11, changedKernels=[0, 1, 2, 3], guards=guards,
                   kernelHashes=[{k: row[k] for k in ('kernel', 'beforeSha256', 'afterSha256')} for row in rows],
                   compiler=str(COMPILER), compileChecks=compile_rows, equivalence=proof,
                   generatorSha256=sha(Path(__file__).read_bytes()),
                   canonicalOrDeviceModified=False, deviceAcceptance=False)
    for name, value in (('manifest.json', receipt), ('equivalence.json', proof), ('compile.json', compile_rows)):
        with (output / name).open('x', encoding='utf-8') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')
    print(json.dumps({k: receipt[k] for k in ('status', 'candidate', 'candidateSha256',
                     'objectAfterSha256', 'preservedRawObjects', 'fullContainerInverseExact')}, indent=2))


if __name__ == '__main__':
    main()
