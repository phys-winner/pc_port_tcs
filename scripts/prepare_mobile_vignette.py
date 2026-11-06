"""Fold the authored unmasked vignette into rev15 BCG; produce a guarded work-only candidate."""
import argparse
import copy
import hashlib
import json
import struct
import subprocess
from pathlib import Path

import numpy as np
import UnityPy

from prepare_bcg_lut import sha, VALIDATOR
from prepare_depth_precision import container_layout
from prepare_sampler_locations import replace_backend, split_stages
from unity_shader_blob import read_programs, encode_record

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/a64-mobile-revision15/app/obb/assets/bin/Data/sharedassets0.assets'
CONTAINER = '6b7e69c0fd8a88000bcde8843b6c35383042de76ab05c85cd69437c4fbff3326'
BCG = '5e421bd07680ee31aa2e1c2de569172d418cdee8f02ac2ee38bcf3898944ce1a'
AUTHOR = 'a048fddc8c498b38e046cae1aa645449668a1e202dbb153adf2468dcbc1202aa'
AUTHOR_CODE = '70644899a2f53c2f51e71208e9dee60bc49c6d1b618f6dd1a17f2634a81f7673'
PROFILE = '1f12b0a3c32b3f068d4ad81eda2258c6e0787b7543e043b7b2c27491f488f4d3'
DECLARATIONS = '\nuniform highp vec4 _ClosingShiftVignette;\nuniform highp vec4 _ClosingShiftVignetteColor;'
CALL = '\nc.xyz = ClosingShift_AuthorVignette(c.xyz, vs_TEXCOORD0.xy);'
FUNCTION = '''highp vec3 ClosingShift_AuthorVignette(highp vec3 rgb, highp vec2 uv)
{
if (_ClosingShiftVignette.w <= 0.0 && _ClosingShiftVignetteColor.w <= 0.0) return rgb;
highp vec2 delta = uv - _ClosingShiftVignette.xy;
delta.y *= _ClosingShiftVignette.z;
highp float weight = clamp(_ClosingShiftVignette.w * dot(delta, delta) + _ClosingShiftVignetteColor.w, 0.0, 1.0);
highp float luminance = dot(rgb, vec3(0.298999995, 0.587000012, 0.114));
return rgb + weight * (luminance * _ClosingShiftVignetteColor.xyz - rgb);
}
'''


def author_proof(env):
    assert sha(env.file.objects[809].get_raw_data()) == AUTHOR
    record = read_programs(env.file.objects[809].read_typetree(), 9)[1032]
    assert sha(record['program_code']) == AUTHOR_CODE
    assert set(record['keywords'] + record['local_keywords']) == {
        'BEAUTIFY_LUT', 'BEAUTIFY_TONEMAP_ACES', 'BEAUTIFY_VIGNETTING', 'BEAUTIFY_PURKINJE'}
    text = record['program_code'].decode()
    for token in (
        'b.y = dot(a.xyw, vec3(0.298999995, 0.587000012, 0.114));',
        'b.xyz = b.yyy * _Vignetting.xyz + (-a.xyw);',
        'c.xy = vs_TEXCOORD2.xy + (-_FXData.xy);',
        'c.z = c.y * _VignettingAspectRatio;',
        'd = dot(c.xz, c.xz);',
        'd = _Vignetting.w * d + _Purkinje.z;',
        'd = clamp(d, 0.0, 1.0);',
        'a.xyw = vec3(d) * b.xyz + a.xyw;'):
        assert token in text, token
    raw = env.file.objects[2309].get_raw_data()
    assert sha(raw) == PROFILE and len(raw) == 884
    assert raw[144] == 1 and raw[168] == 0
    color = struct.unpack_from('<4f', raw, 148)
    assert color == (np.float32(.3), np.float32(.3), np.float32(.3), np.float32(.05))
    assert struct.unpack_from('<f', raw, 164)[0] == 0
    assert struct.unpack_from('<2f', raw, 172) == (1, 0)
    assert struct.unpack_from('<iq', raw, 184) == (0, 0)
    assert struct.unpack_from('<2f', raw, 196) == (.5, .5)
    return dict(shader809SHA256=AUTHOR, shaderRecord=1032, shaderCodeSHA256=AUTHOR_CODE,
                profile2309SHA256=PROFILE, authoredVignette=[.5, .5, 1, float(color[3] * 32)],
                authoredColorFade=[*color[:3], 0], extraTextureSamples=0,
                purkinjeHistoryRestored=False, depthSharpeningRestored=False)


def patch(obj):
    assert obj.path_id == 838 and obj.type.name == 'Shader' and sha(obj.get_raw_data()) == BCG
    tree = obj.read_typetree()
    assert tree['platforms'] == [9] and tree['m_ParsedForm']['m_Name'] == 'Hidden/Colorful/Brightness Contrast Gamma'
    records = read_programs(tree, 9)
    assert len(records) == 1 and encode_record(records[0]) == records[0]['record_bytes']
    original = records[0]
    record = copy.deepcopy(original)
    text = record['program_code'].decode()
    vertex, fragment = split_stages(text)
    declaration = 'uniform highp vec4 _ClosingShiftLut;'
    call = 'c.xyz = ClosingShift_AuthorLut(c.xyz);'
    assert fragment.count(declaration) == fragment.count(call) == fragment.count('void main()\n') == 1
    modified = fragment.replace(declaration, declaration + DECLARATIONS, 1)
    modified = modified.replace('void main()\n', FUNCTION + 'void main()\n', 1).replace(call, call + CALL, 1)
    assert modified.replace(DECLARATIONS, '', 1).replace(FUNCTION, '', 1).replace(CALL, '', 1) == fragment
    assert modified[modified.index('b.xyz = _Coeffs.xyz;'):] == fragment[fragment.index('b.xyz = _Coeffs.xyz;'):]
    assert modified.count('texture(') == fragment.count('texture(')
    record['program_code'] = text.replace(fragment, modified, 1).encode()
    assert split_stages(record['program_code'].decode())[0] == vertex
    group = next(group for group in record['tail']['groups'] if group['name'] == '$Globals')
    assert group['used_size'] == 176 and len(group['parameters']) == 5
    for name, offset in (('_ClosingShiftVignette', 176), ('_ClosingShiftVignetteColor', 192)):
        group['parameters'].append(dict(name=name, type=0, rows=1, columns=4, matrix=0, array_size=0, byte_offset=offset))
    group['used_size'] = 208
    assert record['tail']['resources'] == original['tail']['resources']
    assert record['tail']['channels'] == original['tail']['channels']
    encoded = encode_record(record)
    target = replace_backend(tree, [encoded])
    props = target['m_ParsedForm']['m_PropInfo']['m_Props']
    assert [prop['m_Name'] for prop in props] == ['_MainTex', '_BCG', '_Coeffs', '_LUTTex', '_ClosingShiftLut']
    for name, description, values in (
        ('_ClosingShiftVignette', 'Vignette center, aspect, intensity', (.5, .5, 1, 0)),
        ('_ClosingShiftVignetteColor', 'Vignette RGB and fade', (.3, .3, .3, 0))):
        prop = copy.deepcopy(props[1])
        prop.update(m_Name=name, m_Description=description)
        for i, value in enumerate(values): prop['m_DefValue[%d]' % i] = value
        props.append(prop)
    for subshader in target['m_ParsedForm']['m_SubShaders']:
        for shaderpass in subshader['m_Passes']:
            assert shaderpass['m_Platforms'] == [9] and len(shaderpass['m_EditorDataHash']) == 1
            assert max(pair[1] for pair in shaderpass['m_NameIndices']) == 9
            shaderpass['m_NameIndices'] += [['_ClosingShiftVignette', 10], ['_ClosingShiftVignetteColor', 11]]
            shaderpass['m_EditorDataHash'][0] = {'bytes[%d]' % i: value for i, value in enumerate(hashlib.md5(encoded).digest())}
    raw = bytes(obj.save_typetree(target))
    check = read_programs(target, 9)[0]
    assert check['program_code'] == record['program_code'] and check['tail'] == record['tail']
    return raw, record['program_code']


def numerical():
    def folded(rgb, uv, params, tint):
        if params[3] <= 0 and tint[3] <= 0: return rgb
        delta = uv - params[:2]
        delta[1] *= params[2]
        weight = np.clip(params[3] * np.dot(delta, delta) + tint[3], 0, 1)
        luminance = np.dot(rgb, [.298999995, .587000012, .114])
        return rgb + weight * (luminance * tint[:3] - rgb)
    def reference(rgb, uv, params, tint):
        # Independent scalar evaluation of record1032's isolated vignette registers.
        luminance = rgb[0] * .298999995 + rgb[1] * .587000012 + rgb[2] * .114
        radius = (uv[0] - params[0]) ** 2 + ((uv[1] - params[1]) * params[2]) ** 2
        weight = min(1, max(0, params[3] * radius + tint[3]))
        return np.array([(1 - weight) * rgb[i] + weight * luminance * tint[i] for i in range(3)])
    random = np.random.default_rng(1032)
    cases = 0
    worst = 0
    for _ in range(2048):
        rgb = random.uniform(0, 2, 3)
        uv = random.uniform(0, 1, 2)
        params = np.array([*random.uniform(.2, .8, 2), random.uniform(.5, 2), random.uniform(0, 32)])
        tint = np.array([*random.uniform(0, 1, 3), random.uniform(0, 1)])
        actual = folded(rgb, uv, params, tint)
        expected = reference(rgb, uv, params, tint)
        worst = max(worst, float(np.max(np.abs(actual - expected))))
        assert np.isfinite(actual).all()
        params[3] = tint[3] = 0
        assert np.array_equal(folded(rgb, uv, params, tint), rgb)
        cases += 2
    assert worst < 1e-12, worst
    params = np.array([.5, .5, 1, 1.6])
    tint = np.array([.3, .3, .3, 0])
    rgb = np.repeat(.5, 3)
    samples = []
    for uv, expected in (([.5, .5], .5), ([0, .5], .36), ([0, 0], .22), ([1, 1], .22)):
        actual = folded(rgb, np.array(uv), params, tint)
        assert np.max(np.abs(actual - expected)) < 1e-8
        samples.append(dict(uv=uv, rgb=actual.tolist()))
        cases += 1
    params[3] = 0; tint[3] = 1
    assert np.allclose(folded(rgb, np.array([.5, .5]), params, tint), .15, atol=1e-8)
    cases += 1
    return dict(cases=cases, maxAbsoluteErrorVsScalarRegisters=worst,
                exactDisabledIdentity=True, defaultSamples=samples)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=ROOT / 'work/mobile_vignette16')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    tests = numerical()
    if args.self_test:
        print(json.dumps(tests, indent=2)); return
    output = args.output.resolve()
    assert output.is_relative_to(ROOT / 'work') and not output.exists() and not args.output.is_symlink()
    source_path = args.source.resolve(strict=True)
    assert not args.source.is_symlink()
    source = source_path.read_bytes()
    assert sha(source) == CONTAINER, 'Expected exact accepted revision15 container'
    env = UnityPy.load(source)
    snapshot = {i: sha(obj.get_raw_data()) for i, obj in env.file.objects.items()}
    layout = container_layout(env.file)
    proof = author_proof(env)
    raw, code = patch(env.file.objects[838])
    env.file.objects[838].set_raw_data(raw)
    result = env.file.save()
    check = UnityPy.load(result)
    assert container_layout(check.file) == layout and check.file.objects.keys() == snapshot.keys()
    for i, obj in check.file.objects.items(): assert sha(obj.get_raw_data()) == (sha(raw) if i == 838 else snapshot[i]), i
    check.file.reader.dispose(); env.file.reader.dispose()
    assert sha(source_path.read_bytes()) == CONTAINER
    output.mkdir(parents=True)
    vertex, fragment = split_stages(code.decode())
    (output / 'shader.vert').write_text(vertex)
    (output / 'shader.frag').write_text(fragment)
    link = subprocess.run([str(VALIDATOR), '-l', str(output / 'shader.vert'), str(output / 'shader.frag')], capture_output=True, text=True)
    (output / 'link.log').write_text(link.stdout + link.stderr)
    assert link.returncode == 0, link.stdout + link.stderr
    (output / 'sharedassets0.assets').write_bytes(result)
    (output / 'sharedassets0.assets.838.shader').write_bytes(raw)
    receipt = dict(format='closing-shift-mobile-vignette-bcg-v1', source=str(source_path), sourceSHA256=CONTAINER,
                   originalShader838SHA256=BCG, shader838SHA256=sha(raw), containerSHA256=sha(result),
                   unrelatedRawObjectsPreserved=len(snapshot) - 1, author=proof, numerical=tests,
                   glslLinked=True, additionalTextureSamples=0, additionalPasses=0, additionalDepthReads=0,
                   existingACESLutAndBCGByteExact=True, inertUntilRuntimeBound=True, canonicalOrDeviceModified=False)
    (output / 'manifest.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    if not __debug__: raise RuntimeError('Strict shader assertions require Python without -O')
    main()
