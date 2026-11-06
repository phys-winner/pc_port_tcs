"""Prepare a work-only HBAO GLES origin candidate for the non-temporal opaque path.

Device diagnostic16 isolated an unmatched pass8 flip in both Forward/Deferred
and native/scaled rendering. Pass8 maps y to 1-y; pass9 uses the runtime GLES
identity _UVTransform. Correct AO0, blur6 and copy8 together so every intermediate
has the same bottom-up origin. Do not touch runtime transforms, fragment math,
the unvalidated deinterleaved/temporal paths, or other injection stages.

Required runtime scope: BeforeImageEffectsOpaque, Deinterleaving.Disabled,
temporal disabled. This is a candidate, not a device acceptance or promotion.
"""
import argparse
import copy
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import re
import subprocess

import UnityPy

from prepare_depth_precision import container_layout, update_editor_hashes
from prepare_sampler_locations import replace_backend, split_stages, VALIDATOR
from unity_shader_blob import read_programs, encode_record

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/a64-mobile-revision15/app/obb/assets/bin/Data/sharedassets0.assets'
CONTAINERS = {
    '6b7e69c0fd8a88000bcde8843b6c35383042de76ab05c85cd69437c4fbff3326': 'revision15',
    '79db7b9136089b619b1a9adab2840ffbc00afbb10efedff0396290d92858a9ea': 'revision16-work-vignette',
}
SHADER = '4e9931e36509bd418afd8176148abbfaa4957d82e5c82c367768bf242d869c25'
PASS_IDS = {0: list(range(0, 1152)), 6: list(range(1353, 1369)), 8: [1375]}
AFFINE = re.compile(r'(?m)^vs_TEXCOORD0\.xy = (\w+)\.xy \* vec2\(0\.5, -0\.5\) \+ vec2\(0\.0, 1\.0\);$')
OLD = 'vec2(0.5, -0.5) + vec2(0.0, 1.0)'
NEW = 'vec2(0.5, 0.5) + vec2(0.0, 0.0)'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def patch_code(code):
    text = code.decode()
    vertex, fragment = split_stages(text)
    assert '_UVTransform' not in vertex and 'hlslcc_mtx' not in vertex
    assert vertex.count('gl_Position.xy = in_POSITION0.xy;') == 1
    assert vertex.count('gl_Position.zw = vec2(0.0, 1.0);') == 1
    matches = list(AFFINE.finditer(vertex))
    assert len(matches) == 1, 'Reviewed HBAO triangle mapping changed'
    temp = matches[0][1]
    assert vertex.count(f'{temp}.xy = in_POSITION0.xy + vec2(1.0, 1.0);') == 1
    assert vertex.count(OLD) == 1 and NEW not in vertex
    changed = vertex.replace(OLD, NEW, 1)
    assert changed.replace(NEW, OLD, 1) == vertex
    assert text.count(vertex) == 1
    result = text.replace(vertex, changed, 1).encode()
    assert split_stages(result.decode())[1] == fragment
    return result


def numerical():
    # Independently evaluate the raster triangle at pixel centers. The copied
    # asymmetric image and an arbitrary AO-depth function must address the same
    # source row after the runtime-identity composite. The old expression fails.
    cases = 0
    rejected_old = 0
    for width, height in ((4, 3), (8, 8), (1390, 632), (2780, 1264)):
        for x, y in ((0, 0), (width - 1, height - 1), (width // 3, height // 5)):
            u, v = (x + .5) / width, (y + .5) / height
            clip_x, clip_y = 2 * u - 1, 2 * v - 1
            new_uv = ((clip_x + 1) * .5, (clip_y + 1) * .5)
            old_uv = ((clip_x + 1) * .5, 1 - (clip_y + 1) * .5)
            expected_image = (u, v, u + 3 * v)
            actual_image = (new_uv[0], new_uv[1], new_uv[0] + 3 * new_uv[1])
            assert max(abs(a - b) for a, b in zip(expected_image, actual_image)) < 1e-12
            expected_ao = 7 * u * u + 2 * v + u * v
            actual_ao = 7 * new_uv[0] ** 2 + 2 * new_uv[1] + new_uv[0] * new_uv[1]
            assert abs(expected_ao - actual_ao) < 1e-12
            rejected_old += abs(old_uv[1] - v) > 1e-8
            cases += 2
    assert rejected_old >= 8
    # Two old blur flips cancel; removing both preserves that parity while
    # keeping the horizontal and vertical blur in the same coordinate system.
    for v in (0, .125, .25, .75, .875, 1):
        assert abs((1 - (1 - v)) - v) < 1e-12
        cases += 1
    sample = ('#ifdef VERTEX\n#version 310 es\nin vec4 in_POSITION0;\nout vec2 vs_TEXCOORD0;\n'
              'vec2 a;\nvoid main()\n{\ngl_Position.xy = in_POSITION0.xy;\n'
              'gl_Position.zw = vec2(0.0, 1.0);\na.xy = in_POSITION0.xy + vec2(1.0, 1.0);\n'
              'vs_TEXCOORD0.xy = a.xy * ' + OLD + ';\n}\n#endif\n'
              '#ifdef FRAGMENT\n#version 310 es\nprecision highp float;\n'
              'out vec4 color;\nvoid main(){color=vec4(1);}\n#endif\n').encode()
    assert patch_code(sample) != sample
    for invalid in (sample.replace(b'0.5, -0.5', b'0.5, -0.25'), sample.replace(b'a.xy *', b'b.xy *')):
        try:
            patch_code(invalid)
        except AssertionError:
            cases += 1
        else:
            raise AssertionError('Unrecognized mapping accepted')
    return dict(cases=cases, originalMappingNegativeControls=rejected_old,
                copyAndAoAddressSameSourceRow=True, twoBlurPassParityPreserved=True)


def patch(obj):
    assert obj.path_id == 829 and obj.type.name == 'Shader' and sha(obj.get_raw_data()) == SHADER
    tree = obj.read_typetree()
    assert tree['platforms'] == [9] and tree['m_ParsedForm']['m_Name'] == 'Hidden/HBAO'
    records = read_programs(tree, 9)
    assert len(records) == 1418
    passes = tree['m_ParsedForm']['m_SubShaders'][0]['m_Passes']
    assert len(passes) == 13
    for pass_id, expected in PASS_IDS.items():
        assert sorted({p['m_BlobIndex'] for p in passes[pass_id]['progVertex']['m_SubPrograms']}) == expected
    copy_vertex, copy_fragment = split_stages(records[1375]['program_code'].decode())
    assert 'SV_Target0 = texture(_MainTex, vs_TEXCOORD0.xy);' in copy_fragment
    composite_vertex, _ = split_stages(records[1376]['program_code'].decode())
    assert 'a.xy = a.xy * _UVTransform.xy;' in composite_vertex
    assert 'vs_TEXCOORD0.xy = a.xy * vec2(0.5, 0.5) + _UVTransform.zw;' in composite_vertex
    selected = {i for ids in PASS_IDS.values() for i in ids}
    encoded, codes, changes = [], {}, []
    for index, record in enumerate(records):
        assert encode_record(record) == record['record_bytes']
        changed = copy.deepcopy(record)
        if index in selected:
            changed['program_code'] = patch_code(record['program_code'])
            assert changed['tail'] == record['tail']
            codes[sha(changed['program_code'])] = changed['program_code']
            changes.append(dict(index=index, oldCodeSHA256=sha(record['program_code']),
                                codeSHA256=sha(changed['program_code']),
                                fragmentSHA256=sha(split_stages(record['program_code'].decode())[1].encode()),
                                bindingTailSHA256=sha(record['tail_bytes'])))
        encoded.append(encode_record(changed))
        assert encode_record({**changed, 'program_code': record['program_code']}) == record['record_bytes']
    target = replace_backend(tree, encoded)
    hashes = update_editor_hashes(tree, target, encoded, selected)
    raw = bytes(obj.save_typetree(target))
    reread = read_programs(target, 9)
    assert len(reread) == len(records)
    for index, record in enumerate(reread):
        assert encode_record(record) == encoded[index]
        if index not in selected: assert record['record_bytes'] == records[index]['record_bytes']
    assert len(changes) == 1169
    return raw, codes, changes, hashes


def validate(item, output):
    identity, code = item
    folder = output / 'validated' / identity
    folder.mkdir(parents=True)
    vertex, fragment = split_stages(code.decode())
    v, f = folder / 'shader.vert', folder / 'shader.frag'
    v.write_text(vertex); f.write_text(fragment)
    result = subprocess.run([str(VALIDATOR), '-l', str(v), str(f)], capture_output=True, text=True)
    (folder / 'link.log').write_text(result.stdout + result.stderr)
    assert result.returncode == 0, str(folder) + '\n' + result.stdout + result.stderr
    return identity


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=ROOT / 'work/mobile_hbao16')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    tests = numerical()
    if args.self_test:
        print(json.dumps(tests, indent=2)); return
    output = args.output.resolve()
    assert output.is_relative_to(ROOT / 'work') and not output.exists() and not args.output.is_symlink()
    source_path = args.source.resolve(strict=True)
    assert not args.source.is_symlink()
    source = source_path.read_bytes(); source_sha = sha(source)
    assert source_sha in CONTAINERS, 'Expected frozen revision15 or work-vignette16 container'
    env = UnityPy.load(source)
    snapshot = {i: sha(obj.get_raw_data()) for i, obj in env.file.objects.items()}
    layout = container_layout(env.file)
    raw, codes, changes, editor_hashes = patch(env.file.objects[829])
    env.file.objects[829].set_raw_data(raw)
    result = env.file.save(); check = UnityPy.load(result)
    assert container_layout(check.file) == layout and check.file.objects.keys() == snapshot.keys()
    for i, obj in check.file.objects.items(): assert sha(obj.get_raw_data()) == (sha(raw) if i == 829 else snapshot[i]), i
    check.file.reader.dispose(); env.file.reader.dispose()
    assert sha(source_path.read_bytes()) == source_sha and VALIDATOR.is_file()
    output.mkdir(parents=True)
    with ThreadPoolExecutor(max_workers=4) as pool:
        validated = list(pool.map(lambda item: validate(item, output), codes.items()))
    (output / 'sharedassets0.assets').write_bytes(result)
    (output / 'sharedassets0.assets.829.shader').write_bytes(raw)
    receipt = dict(format='closing-shift-hbao-gles-opaque-origin-candidate-v1', source=str(source_path),
                   sourceSHA256=source_sha, sourceProfile=CONTAINERS[source_sha], shader829PreimageSHA256=SHADER,
                   shader829SHA256=sha(raw), containerSHA256=sha(result), changedRecords=changes,
                   changedPasses=list(PASS_IDS), unchangedRecords=1418-len(changes), editorHashes=editor_hashes,
                   unrelatedRawObjectsPreserved=len(snapshot)-1, linkedProgramCount=len(validated), numerical=tests,
                   requiredScope=dict(pipelineStage='BeforeImageEffectsOpaque', deinterleaving='Disabled', temporal=False),
                   fragmentMathAndBindingsExact=True, runtimeUVTransformExact=True,
                   deviceAcceptance=False, canonicalOrDeviceModified=False)
    (output / 'manifest.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({k:v for k,v in receipt.items() if k not in ('changedRecords','editorHashes')}, indent=2))


if __name__ == '__main__':
    if not __debug__: raise RuntimeError('Strict shader assertions require Python without -O')
    main()
