"""Keep Aura lighting voxel addresses integer in a private COUNTER25 candidate.

Only ComputeShader1826's twelve GLES source strings change. All ten payload
word reads, seven SSBOs, constant-buffer metadata and floating lighting code
remain exact. This removes an integer/float-bitcast portability hazard; it
does not establish that the Mali GPU actually flushed these values to zero.
"""
from __future__ import annotations

import argparse
import copy
import difflib
import hashlib
import json
import os
import re
import struct
import subprocess
from pathlib import Path

import UnityPy
from prepare_depth_precision import container_layout


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/aura_counter25/resources.assets'
WORK = ROOT / 'work/aura_coords26'
COMPILER = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/ndk/27.2.12479018/shader-tools/windows-x86_64/glslc.exe'
BEFORE = 'dc8a7cc8926da8023bc34d78db6b6f0e765637d687344d15ec67ff0ad62dff5c'
OBJECT_BEFORE = 'fdc53fc9692fe8c54e13d566e8a237c9ae9d794e9969d83d5532af1aa64f61bd'
CODE_BEFORE = (
    '81175900071240c74e69b4fe937534c81422b772235842f0da4bf1165ce24c65',
    'ce776fc09c2ba091e9dbc2730086ea9e7a2867c25b1039321ad463c492b13224',
    '327097ceabe5b964a45bc1249d21f22d81011fef006484adac488185d0c451a8',
    'a06dd646e7d5a78019bf1dc11e656469dfb6a815d8b0ec42f2422e27692011fa',
    '07e2de3b225eb9cf00bfddf23a0681ad635bf6eb9ab4e4fa0b9b41198d68a762',
    '63f5050572e2b3cea0b6a77466c829e3d1be7f6ad087c9b8c25a5a9334650142',
    'e39ecb766cb7fa8b593259184507ba25f702e82d90f4ee59829bfeca4ace66bb',
    '0cdf554651d6b8f346c93274ae4e66200835f4015dc0f2725b552cab7124b581',
    'b158dc7b33a91283168b6a95e20b6801f0e2b2094143b8921a7f7b728191f512',
    '19111336bca7b18fbdb4dfd313914463729f0c6f7f5ba9b74f62e5481cc615b6',
    '63c690b8e73e7fdb39b0a182408f8d7449a37aa3f8324c5cc09a0abd42e485d9',
    '5f13d92cd3c8af858f38de176c9ed2d141510b5a8063ef62bffd399342327ab6',
)
INDEX_SUFFIX = (54, 53, 53, 55, 54, 54, 54, 53, 53, 55, 54, 54)
STEREO = {3, 4, 5, 9, 10, 11}
INPUT_NAMES = ('dispatchSizeBuffer', 'visibleCellsAmountBuffer', 'visibleCellsBuffer',
               'volumeDataBuffer', 'directionalLightDataBuffer', 'spotLightDataBuffer', 'pointLightDataBuffer')


def require(ok, message):
    if not ok:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def cases(tree):
    variants = [v for v in tree['variants'] if v['targetRenderer'] == 11]
    require(len(variants) == 1, 'Expected one GLES variant')
    variant = variants[0]
    require(variant['targetLevel'] == 3 and variant['resourcesResolved'] is True,
            'Pinned GLES resource contract changed')
    require(len(variant['kernels']) == 12, 'Expected twelve lighting kernels')
    result = []
    for kernel in variant['kernels']:
        require(kernel['name'] == 'ComputeDataBuffer' and len(kernel['variantMap']) == 1, 'Kernel identity changed')
        case = kernel['variantMap'][0][1]
        require(case['threadGroupSize'] == [8, 8, 2] and tuple(b['name'] for b in case['inBuffers']) == INPUT_NAMES,
                'Seven SSBO/group contract changed')
        result.append(case)
    return result


def patch_text(source, index):
    require(0 <= index < 12 and 'portCell' not in source, 'Unknown kernel or already patched source')
    aliases = re.findall(r'\bu_xlat1(?:\.[xyzw]+)?\b', source)
    expected = {'u_xlat1', 'u_xlat1.w', 'u_xlat1.x', 'u_xlat1.xyz'} if index in STEREO else {
        'u_xlat1', 'u_xlat1.w', 'u_xlat1.www', 'u_xlat1.xyz'}
    require(set(aliases) == expected and aliases.count('u_xlat1') == 2
            and aliases.count('u_xlat1.xyz') == 1, 'Unaccounted coordinate alias/use')
    require(source.count('vec4 u_xlat1;') == 1, 'Coordinate register declaration changed')
    address = f'u_xlatu{INDEX_SUFFIX[index]}'
    words = [f'visibleCellsBuffer_buf[{address}].value[(0 >> 2) + {i}]' for i in range(4)]
    old_load = '    u_xlat1 = vec4(' + ', '.join('uintBitsToFloat(' + w + ')' for w in words) + ');'
    new_load = ('    uvec3 portCell = uvec3(' + ', '.join(words[:3]) + ');\n'
                '    u_xlat1.w = uintBitsToFloat(' + words[3] + ');')
    output = 'u_xlat2' if index in STEREO else 'u_xlat0'
    old_store = f'    imageStore(lightingTexture, floatBitsToInt(u_xlat1.xyz), {output});'
    new_store = f'    imageStore(lightingTexture, ivec3(portCell), {output});'
    edits = [(old_load, new_load), (old_store, new_store)]
    stereo_read = 'uint(floatBitsToUint(u_xlat1.x))'
    require(source.count(stereo_read) == (1 if index in STEREO else 0), 'Stereo coordinate anchor changed')
    if index in STEREO:
        edits.append((stereo_read, 'portCell.x'))
    target = source
    for old, new in edits:
        require(target.count(old) == 1, 'Exact coordinate anchor count differs')
        target = target.replace(old, new)
    inverse = target
    for old, new in reversed(edits):
        require(inverse.count(new) == 1, 'Inverse coordinate anchor count differs')
        inverse = inverse.replace(new, old)
    require(inverse == source, 'Source inverse changed unrelated floating code')
    after_aliases = re.findall(r'\bu_xlat1(?:\.[xyzw]+)?\b', target)
    require(set(after_aliases) <= {'u_xlat1', 'u_xlat1.w', 'u_xlat1.www'}
            and after_aliases.count('u_xlat1') == 1, 'Float coordinate alias remains')
    reads = lambda s: re.findall(r'visibleCellsBuffer_buf\[[^\]]+\]\.value\[\([^)]*\)(?: \+ \d+)?\]', s)
    require(len(reads(source)) == 10 and reads(source) == reads(target), 'Payload word addresses/order changed')
    require(source[:source.index('void main()')] == target[:target.index('void main()')],
            'Declarations or buffer bindings changed')
    require(target.count('portCell') == 2 + (index in STEREO), 'Unexpected typed-coordinate alias')
    return target, edits


def patch_tree(tree):
    target = copy.deepcopy(tree)
    old_cases, new_cases = cases(tree), cases(target)
    rows = []
    for index, old_case in enumerate(old_cases):
        raw = bytes(old_case['code'])
        require(sha(raw) == CODE_BEFORE[index] and raw.endswith(b'\0'), 'Exact source kernel required: ' + str(index))
        source = raw[:-1].decode('utf-8')
        changed, edits = patch_text(source, index)
        new_cases[index]['code'] = list(changed.encode('utf-8') + b'\0')
        rows.append({'kernel': index, 'before_sha256': sha(raw), 'after_sha256': sha(bytes(new_cases[index]['code'])),
                     'source': source, 'target': changed, 'edits': edits})
    inverse = copy.deepcopy(target)
    for old, restored in zip(old_cases, cases(inverse)):
        restored['code'] = old['code']
    require(inverse == tree, 'Unrelated backend, kernel, or binding metadata changed')
    return target, rows


def prepare(raw):
    require(len(raw) == 106192864 and sha(raw) == BEFORE, 'Exact COUNTER25 resources preimage required')
    env = UnityPy.load(raw)
    try:
        obj = env.file.objects[1826]
        require(obj.type.name == 'ComputeShader' and sha(obj.get_raw_data()) == OBJECT_BEFORE,
                'Exact lighting object1826 required')
        tree = obj.read_typetree()
        require(tree['m_Name'] == 'ComputeDataComputeShader', 'Lighting shader identity changed')
        target, rows = patch_tree(tree)
        obj.save_typetree(target)
        return env.file.save(), rows
    finally:
        env.file.reader.dispose()


def verify(before, after):
    original, candidate = UnityPy.load(before), UnityPy.load(after)
    try:
        require(sha(before) == BEFORE and container_layout(original.file) == container_layout(candidate.file),
                'Source hash or container identity changed')
        expected, _ = patch_tree(original.file.objects[1826].read_typetree())
        obj = candidate.file.objects[1826]
        require(obj.read_typetree() == expected, 'Candidate is not the exact coordinate-only patch')
        for pid, old in original.file.objects.items():
            if pid != 1826:
                require(old.get_raw_data() == candidate.file.objects[pid].get_raw_data(), 'Unrelated raw object changed: ' + str(pid))
        patched_hash = sha(obj.get_raw_data())
        obj.set_raw_data(original.file.objects[1826].get_raw_data())
        require(candidate.file.save() == before, 'Full container inverse differs')
        return {'other_raw_objects_unchanged': len(original.file.objects) - 1,
                'object1826_after_sha256': patched_hash, 'full_container_inverse_exact': True,
                'all_metadata_and_bindings_exact': True, 'ssbo_inputs_per_kernel': 7,
                'all_ten_payload_reads_exact': True, 'floating_lighting_source_unchanged': True}
    finally:
        original.file.reader.dispose()
        candidate.file.reader.dispose()


def fixtures():
    """Independent address/eye model; payload float words are opaque preserved bits."""
    signed = lambda w: w if w < 0x80000000 else w - 0x100000000
    old_address = lambda words, ftz: tuple(signed(0 if ftz and 0 < w < 0x00800000 else w) for w in words[:3])
    # Nontrivial float encodings ensure word3 is not accidentally discarded when
    # splitting the original vec4 load. No floating lighting math is emulated.
    float_words = (0x3f000000, 0x3e800000, 0x3f7f0000, 0x3f400000, 0x3e000000, 0x3f100000, 0x3f600000)
    checked = 0
    wrong_if_ftz = 0
    address_hash = hashlib.sha256()
    for z in range(64):
        for y in range(40):
            for x in range(64):
                words = struct.unpack('<10I', struct.pack('<10I', x, y, z, *float_words))
                typed_address = tuple(signed(w) for w in words[:3])
                require(typed_address == (x, y, z) == old_address(words, False), 'Loaded payload address changed')
                require(words[3:] == float_words, 'Seven floating payload words changed')
                address_hash.update(struct.pack('<3i', *typed_address))
                wrong_if_ftz += old_address(words, True) != typed_address
                checked += 1
    require(checked == 163840 and wrong_if_ftz == 163839, 'FTZ positive control did not expose the original hazard')
    # These are compacted/permuted entries, deliberately unrelated to their
    # invocation/array index. The fix must follow loaded XYZ, not reconstruct it.
    compacted = [(63, 39, 63), (0, 1, 47), (32, 20, 5), (1, 0, 0), (0, 0, 0)]
    for index, coords in enumerate(compacted):
        words = (*coords, *float_words)
        require(tuple(signed(w) for w in words[:3]) == coords, 'Compacted address lost')
        if index == 0:
            require(coords != (index, 0, 0), 'Compacted fixture accidentally matches invocation ID')
    eyes = 0
    for width in (64, 128):
        for x in (0, width // 2 - 1, width // 2, width // 2 + 1, width - 1):
            words = (x, 0, 0, *float_words)
            require(((width >> 1) < words[0]) == ((width >> 1) < old_address(words, False)[0]),
                    'Authored strict stereo comparison changed')
            eyes += 1
    return {'complete_grid_addresses': checked, 'compacted_permuted_entries': len(compacted),
            'stereo_boundary_cases': eyes, 'address_stream_sha256': address_hash.hexdigest(),
            'old_address_mismatches_under_ftz_model': wrong_if_ftz,
            'typed_address_mismatches_under_ftz_model': 0,
            'float_words_treated_as_opaque': True, 'device_ftz_observed': False}


def negative_guards(raw, rows):
    passed = []
    def reject(name, action, expected):
        try:
            action()
        except ValueError as error:
            require(expected in str(error), name + ': unexpected rejection: ' + str(error))
        else:
            raise ValueError(name + ': invalid input accepted')
        passed.append(name)
    reject('wrong_container_preimage', lambda: prepare(raw[:-1] + bytes([raw[-1] ^ 1])), 'preimage')
    reject('already_patched_source', lambda: patch_text(rows[0]['target'], 0), 'already patched')
    reject('unaccounted_xyz_alias', lambda: patch_text(rows[0]['source'] + '\nfloat forbidden = u_xlat1.y;\n', 0), 'coordinate alias')
    reject('changed_float_payload_word3', lambda: patch_text(rows[0]['source'].replace(
        'visibleCellsBuffer_buf[u_xlatu54].value[(0 >> 2) + 3]',
        'visibleCellsBuffer_buf[u_xlatu54].value[(0 >> 2) + 2]'), 0), 'anchor count')
    reject('wrong_stereo_coordinate', lambda: patch_text(rows[3]['source'].replace(
        'uint(floatBitsToUint(u_xlat1.x))', 'uint(floatBitsToUint(u_xlat1.y))'), 3), 'coordinate alias')
    return passed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=WORK)
    args = parser.parse_args()
    output = args.output.resolve()
    require((output == WORK or output.is_relative_to(WORK)) and not args.output.is_symlink() and not output.exists(),
            'Output must be a fresh directory under work/aura_coords26')
    require(COMPILER.is_file(), 'Installed GLSL compiler required')
    raw = SOURCE.read_bytes()
    candidate, rows = prepare(raw)
    receipt = verify(raw, candidate)
    guards = negative_guards(raw, rows)
    fixture = fixtures()
    require(sha(SOURCE.read_bytes()) == BEFORE, 'Retained source changed concurrently')
    output.mkdir(parents=True)
    compiled = []
    for row in rows:
        path = output / ('kernel' + str(row['kernel']) + '.comp')
        path.write_text(row['target'], encoding='utf-8', newline='\n')
        result = subprocess.run([str(COMPILER), '--target-env=opengl', '-fshader-stage=compute',
                                 '-fauto-map-locations', '-fauto-bind-uniforms', str(path), '-o',
                                 str(path.with_suffix('.spv'))], capture_output=True, text=True, timeout=30)
        require(result.returncode == 0, 'GLSL compile failed kernel' + str(row['kernel']) + ': ' + result.stderr)
        compiled.append({'kernel': row['kernel'], 'exit_code': result.returncode,
                         'stdout': result.stdout, 'stderr': result.stderr})
        path.with_suffix('.diff').write_text(''.join(difflib.unified_diff(
            row['source'].splitlines(True), row['target'].splitlines(True),
            fromfile='kernel' + str(row['kernel']) + '-source', tofile=path.name)), encoding='utf-8')
    path = output / 'resources.assets'
    path.write_bytes(candidate)
    require(path.read_bytes() == candidate, 'Candidate reread differs')
    verify(raw, path.read_bytes())
    require(sha(SOURCE.read_bytes()) == BEFORE, 'Retained source changed concurrently')
    manifest = {'input': str(SOURCE), 'output': str(path), 'source_sha256': BEFORE, 'output_sha256': sha(candidate),
                'script_sha256': sha(Path(__file__).read_bytes()), 'checks': receipt, 'negative_guards': guards,
                'fixtures': fixture, 'compiler': str(COMPILER), 'compile': compiled,
                'kernels': [{k: v for k, v in row.items() if k not in ('source', 'target')} for row in rows],
                'status': 'private coordinate portability candidate; hardware cause and acceptance unproven',
                'native_library_changed': False, 'managed_code_changed': False,
                'occlusion_temporal_routing_changed': False, 'canonical_or_device_changed': False}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'output': str(path), 'sha256': sha(candidate), 'checks': receipt,
                      'fixtures': fixture, 'compiled_kernels': len(compiled), 'negative_guards': guards}, indent=2))


if __name__ == '__main__':
    main()
