"""Split Aura's independent 512-thread groups into equivalent 128-thread groups for GLES."""
from __future__ import annotations

import argparse
import copy
import json
import math
from pathlib import Path
import os
import re
import shutil
import subprocess
import tempfile

import UnityPy

from apply_compute_objects import apply, checked, sha

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/a64-low-menu-fixed-ui4/app/obb/assets/bin/Data/resources.assets'
VALIDATOR = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/ndk/27.2.12479018/shader-tools/windows-x86_64/glslc.exe'
OBJECTS = {
    1824: ('ApplyBlurFilterComputeShader', '6f600d698a8cf5250d42a5d1c36045888b5f2e916c7d56bc5cbc6f91e506f9e8', 6),
    1825: ('ComputeVisibleCellsBufferComputeShader', '0576e9ca32a602837ffe421edb6d484873860e8d7ac84e9b8d80141431ca7e2b', 1),
    1826: ('ComputeDataComputeShader', '8928e3e746427b93976f269524610adcd72ef92fcd4296faef901adba1930666', 12),
    1828: ('ApplyDenoisingFilterComputeShader', 'f8865464bb0254f6c6aa4569f696352f53097429728c897ba9515020a2d3d601', 3),
}


def coordinate_check():
    # The flattened index uses dispatch X/Y * 8; only split Z so those strides stay authored.
    for gx, gy, gz in [(1, 1, 1), (2, 3, 4), (10, 6, 12)]:
        width, height, depth = gx * 8, gy * 8, gz * 8
        expected = set(range(width * height * depth))
        old = {x + width * (y + height * z) for z in range(depth) for y in range(height) for x in range(width)}
        new = {x + width * (y + height * (group_z * 2 + local_z))
               for group_z in range(gz * 4) for local_z in range(2) for y in range(height) for x in range(width)}
        checked(old == new == expected, 'Split dispatch lost or duplicated a flattened cell')
    for count in [0, 1, 127, 128, 129, 511, 512, 513, 4095, 4096, 4097, 368640, 16777215]:
        groups = math.ceil(count ** (1 / 3) / 8)
        checked(groups ** 3 * 512 == groups * groups * (groups * 4) * 128, 'Indirect split changed coverage')
        checked(groups ** 3 * 512 >= count, 'Indirect dispatch undercovers visible cells')
    return {'exactCoordinateFixtures': 3, 'indirectBoundaryFixtures': 13, 'noLostOrDuplicateCells': True}


def prepare(source, output):
    checked(source.resolve() == SOURCE.resolve(), 'Use the retained immutable accepted shader source')
    checked(output.resolve().is_relative_to(ROOT / 'work'), 'Output must stay inside work')
    output.mkdir(parents=True, exist_ok=True)
    env = UnityPy.load(source.read_bytes())
    results, validations = [], []
    try:
        for pid, (name, expected, count) in OBJECTS.items():
            obj = env.file.objects[pid]
            checked(obj.type.name == 'ComputeShader' and sha(obj.get_raw_data()) == expected, 'Accepted compute preimage changed: ' + str(pid))
            original = obj.read_typetree()
            tree = copy.deepcopy(original)
            checked(tree['m_Name'] == name, 'Shader name changed')
            variants = [v for v in tree['variants'] if v['targetRenderer'] == 11]
            checked(len(variants) == 1, 'Ambiguous GLES variant')
            changed = 0
            for index, kernel in enumerate(variants[0]['kernels']):
                for keyword, case in kernel['variantMap']:
                    if pid == 1825 and kernel['name'] != 'EvaluateDispatchSize':
                        continue
                    code = bytes(case['code'])
                    checked(code.startswith(b'#version 310 es\n') and code.endswith(b'\0'), 'Unexpected GLES framing')
                    text = code[:-1].decode('utf-8')
                    if pid == 1825:
                        checked(case['threadGroupSize'] == [1, 1, 1] and index == 4, 'Indirect producer identity changed')
                        old = 'sizeBuffer_buf[0].value[(0 >> 2) + 2] = uint(u_xlatu0);'
                        new = 'sizeBuffer_buf[0].value[(0 >> 2) + 2] = uint(u_xlatu0) * 4u;'
                    else:
                        checked(case['threadGroupSize'] == [8, 8, 8], 'Authored group changed')
                        checked(set(re.findall(r'\bgl_\w+', text)) == {'gl_GlobalInvocationID'}, 'Group-local semantics need separate porting')
                        checked(not re.search(r'\b(?:shared|barrier|memoryBarrier\w*|groupMemoryBarrier)\b', text), 'Workgroup synchronization needs separate porting')
                        old = 'layout(local_size_x = 8, local_size_y = 8, local_size_z = 8) in;'
                        new = 'layout(local_size_x = 8, local_size_y = 8, local_size_z = 2) in;'
                        case['threadGroupSize'] = [8, 8, 2]
                    checked(text.count(old) == 1, 'Exact shader instruction changed')
                    text = text.replace(old, new)
                    case['code'] = list(text.encode('utf-8') + b'\0')
                    path = output / f'{pid}-{index}.comp'
                    path.write_text(text, encoding='utf-8')
                    validation = subprocess.run([str(VALIDATOR), '--target-env=opengl', '-fshader-stage=compute',
                        '-fauto-map-locations', '-fauto-bind-uniforms', str(path), '-o', str(path.with_suffix('.spv'))],
                        capture_output=True, text=True, timeout=30)
                    checked(validation.returncode == 0, 'GLSL validation failed: ' + validation.stderr)
                    validations.append({'path': str(path.resolve()), 'sha256': sha(text.encode()), 'group': case['threadGroupSize'], 'glslcPassed': True})
                    changed += 1
            checked(changed == count, 'Kernel coverage changed')
            raw = obj.save_typetree(tree)
            path = output / f'resources_{pid}.bin'
            path.write_bytes(raw)
            results.append({'file': 'resources.assets', 'path_id': pid, 'name': name, 'source_object_sha256': expected,
                            'object_file': str(path.resolve()), 'object_sha256': sha(raw), 'changedKernels': changed})
    finally:
        env.file.reader.dispose()
    manifest = {'results': results, 'validations': validations, 'coordinateSelfCheck': coordinate_check(),
                'dispatchContract': 'CPU uses GetKernelThreadGroupSizes; GPU EvaluateDispatchSize multiplies Z groups by4; X/Y flatten strides remain8'}
    path = output / 'manifest.json'
    path.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    return path


def selftest(manifest):
    with tempfile.TemporaryDirectory(prefix='aura128-fixture-', dir=ROOT / 'work') as directory:
        fixture = Path(directory)
        path = fixture / 'resources.assets'
        shutil.copyfile(SOURCE, path)
        before = sha(path.read_bytes())
        ledger = fixture / 'ledger.json'
        apply(manifest, fixture, False, all_ready=True, ledger_path=ledger)
        checked(sha(path.read_bytes()) == before, 'Dry plan modified source')
        first = apply(manifest, fixture, True, all_ready=True, ledger_path=ledger)
        checked(first[0]['changed_path_ids'] == sorted(OBJECTS), 'Incorrect applied object set')
        second = apply(manifest, fixture, True, all_ready=True, ledger_path=ledger)
        checked(not second[0]['changed_path_ids'], 'Application is not idempotent')
        return {'dryPlanPreserved': True, 'idempotent': True, 'first': first, 'coordinates': coordinate_check()}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'work/portable_aura_compute')
    parser.add_argument('--data-root', type=Path, default=ROOT / 'app/obb/assets/bin/Data')
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    manifest = prepare(SOURCE, args.output.resolve())
    report = selftest(manifest) if args.self_test else apply(manifest, args.data_root, args.apply, all_ready=True)
    (args.output / ('selftest.json' if args.self_test else 'staging.json')).write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('AURA128_SELFTEST_PASS' if args.self_test else 'AURA128_APPLIED' if args.apply else 'AURA128_PLAN_PASS')
