"""Restore exact author LUT math after retained ACES; write a guarded work-only candidate."""
import argparse
import copy
import hashlib
import json
import os
import subprocess
from pathlib import Path

import numpy as np
import UnityPy

from unity_shader_blob import read_programs, encode_record
from prepare_sampler_locations import replace_backend, split_stages
from prepare_depth_precision import container_layout

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/a64-mobile-revision7/app/obb/assets/bin/Data/sharedassets0.assets'
OLD = '9043afde6f9c7fd071d42564da0ce879862c864b1aae592cab1cb12bb7ef2a4c'
AUTHOR = 'a048fddc8c498b38e046cae1aa645449668a1e202dbb153adf2468dcbc1202aa'
LUT = 'e13a4f7eebdfbf674eb5ed53058aa6d3cf61af911303152143d40cdf5e443f0c'
AUTHOR_CODE = 'f40d5e8536ae6a8eed50e16789bb753f76818e05e43fdd7adf4d7b027639f566'
VALIDATOR = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/emulator/lib64/vulkan/glslangValidator.exe'
FUNCTION = '''highp vec3 ClosingShift_AuthorLut(highp vec3 rgb)
{
if (_ClosingShiftLut.w <= 0.0) return rgb;
highp vec3 gammaRGB = max(rgb, vec3(0.0));
gammaRGB = exp2(log2(gammaRGB) * vec3(0.416666657));
gammaRGB = max(gammaRGB * vec3(1.05499995) + vec3(-0.0549999997), vec3(0.0));
highp vec3 addressRGB = min(gammaRGB, vec3(1.0));
highp float blue = addressRGB.z * _ClosingShiftLut.z;
highp float slice = floor(blue);
highp vec2 uv = (addressRGB.xy * _ClosingShiftLut.z + vec2(0.5)) * _ClosingShiftLut.xy;
uv.x += slice * _ClosingShiftLut.y;
highp vec3 lowSlice = texture(_LUTTex, uv).xyz;
highp vec3 highSlice = texture(_LUTTex, uv + vec2(_ClosingShiftLut.y, 0.0)).xyz;
highp vec3 graded = (blue - slice) * (highSlice - lowSlice) + lowSlice;
gammaRGB = _ClosingShiftLut.w * (graded - gammaRGB) + gammaRGB;
highp vec3 conversion = gammaRGB * vec3(0.305306017) + vec3(0.682171106);
conversion = gammaRGB * conversion + vec3(0.0125228781);
return gammaRGB * conversion;
}
'''

def sha(data):
    return hashlib.sha256(data).hexdigest()

def author_proof(env):
    author = env.file.objects[809]
    lut = env.file.objects[578]
    assert sha(author.get_raw_data()) == AUTHOR and sha(lut.get_raw_data()) == LUT
    record = read_programs(author.read_typetree(), 9)[1029]
    text = record['program_code'].decode()
    assert sha(record['program_code']) == AUTHOR_CODE
    assert record['local_keywords'] == ['BEAUTIFY_LUT', 'BEAUTIFY_TONEMAP_ACES']
    for token in ('0.416666657', '1.05499995', '-0.0549999997', '0.305306017', '0.682171106', '0.0125228781'):
        assert token in text and token in FUNCTION
    assert 'k.y = c.x * _LUTTex_TexelSize.y + k.z;' in text
    assert 'k.x = k.y + _LUTTex_TexelSize.y;' in text
    assert 'a.xyw = _FXColor.www * b.xyz + a.xyw;' in text
    tree = lut.read_typetree()
    assert (tree['m_Width'], tree['m_Height'], tree['m_TextureFormat'], tree['m_ColorSpace']) == (1024, 32, 3, 0)
    return dict(shader809SHA256=AUTHOR, authorRecord=1029, authorCodeSHA256=AUTHOR_CODE, lut578SHA256=LUT,
                lutName=tree['m_Name'], lutWidth=1024, lutHeight=32, colorSpace='linear data', fullStrength=True)

def patched_record(record):
    text = record['program_code'].decode()
    vertex, fragment = split_stages(text)
    assert text.count('ClosingShift_DisplayACES') == 2 and text.count('texture(') == 1
    declaration = 'UNITY_LOCATION(0) uniform highp sampler2D _MainTex;'
    assert fragment.count(declaration) == 1
    fragment = fragment.replace(declaration, declaration + '\nUNITY_LOCATION(1) uniform highp sampler2D _LUTTex;\nuniform highp vec4 _ClosingShiftLut;', 1)
    call = 'c.xyz = ClosingShift_DisplayACES(c.xyz);'
    assert fragment.count(call) == 1
    fragment = fragment.replace(call, call + '\nc.xyz = ClosingShift_AuthorLut(c.xyz);', 1)
    assert fragment.count('void main()\n') == 1
    fragment = fragment.replace('void main()\n', FUNCTION + 'void main()\n', 1)
    result = copy.deepcopy(record)
    result['program_code'] = text.replace(split_stages(text)[1], fragment, 1).encode()
    assert split_stages(result['program_code'].decode())[0] == vertex
    assert fragment[fragment.index('b.xyz = _Coeffs.xyz;'):] == split_stages(text)[1][split_stages(text)[1].index('b.xyz = _Coeffs.xyz;'):]
    group = next(group for group in result['tail']['groups'] if group['name'] == '$Globals')
    assert group['used_size'] == 160 and len(group['parameters']) == 4
    group['parameters'].append(dict(name='_ClosingShiftLut', type=0, rows=1, columns=4, matrix=0, array_size=0, byte_offset=160))
    group['used_size'] = 176
    assert len(result['tail']['resources']) == 1
    result['tail']['resources'].append(dict(name='_LUTTex', type=0, index=1, extra=1, multisampled=False, dimension=2))
    assert result['tail']['channels'] == record['tail']['channels']
    return result

def patch(obj):
    assert obj.path_id == 838 and obj.type.name == 'Shader' and sha(obj.get_raw_data()) == OLD
    tree = obj.read_typetree()
    assert tree['platforms'] == [9] and tree['m_ParsedForm']['m_Name'] == 'Hidden/Colorful/Brightness Contrast Gamma'
    records = read_programs(tree, 9)
    assert len(records) == 1 and encode_record(records[0]) == records[0]['record_bytes']
    record = patched_record(records[0])
    encoded = encode_record(record)
    target = replace_backend(tree, [encoded])
    props = target['m_ParsedForm']['m_PropInfo']['m_Props']
    assert [prop['m_Name'] for prop in props] == ['_MainTex', '_BCG', '_Coeffs']
    lut = copy.deepcopy(props[0]); lut['m_Name'] = '_LUTTex'; lut['m_Description'] = 'Author color grading LUT'
    params = copy.deepcopy(props[1]); params['m_Name'] = '_ClosingShiftLut'; params['m_Description'] = 'LUT texel size, grid maximum, strength'
    for i, value in enumerate((1/1024, 1/32, 31, 0)):
        params['m_DefValue[%d]' % i] = value
    props.extend([lut, params])
    for subshader in target['m_ParsedForm']['m_SubShaders']:
        for shaderpass in subshader['m_Passes']:
            assert shaderpass['m_Platforms'] == [9] and len(shaderpass['m_EditorDataHash']) == 1
            old_names = list(shaderpass['m_NameIndices'])
            assert max(pair[1] for pair in old_names) == 7
            shaderpass['m_NameIndices'] += [['_LUTTex', 8], ['_ClosingShiftLut', 9]]
            digest = hashlib.md5(encoded).digest()
            shaderpass['m_EditorDataHash'][0] = {'bytes[%d]' % i: value for i, value in enumerate(digest)}
    raw = bytes(obj.save_typetree(target))
    check = read_programs(target, 9)[0]
    assert check['program_code'] == record['program_code'] and check['tail'] == record['tail']
    return raw, record['program_code']

def numerical(lut_image):
    pixels = np.asarray(lut_image.convert('RGB'), dtype=np.float64)[::-1] / 255
    assert pixels.shape == (32, 1024, 3)
    def sample(uv):
        point = uv * [1024, 32] - .5
        base = np.floor(point).astype(int); frac = point - base
        a = pixels[np.clip(base[1], 0, 31), np.clip(base[0], 0, 1023)]
        b = pixels[np.clip(base[1], 0, 31), np.clip(base[0]+1, 0, 1023)]
        c = pixels[np.clip(base[1]+1, 0, 31), np.clip(base[0], 0, 1023)]
        d = pixels[np.clip(base[1]+1, 0, 31), np.clip(base[0]+1, 0, 1023)]
        return (a+(b-a)*frac[0]) + ((c+(d-c)*frac[0])-(a+(b-a)*frac[0]))*frac[1]
    def folded(rgb, strength):
        if strength <= 0: return rgb
        gamma = np.maximum(np.maximum(rgb, 0)**float(np.float32(.416666657))*float(np.float32(1.05499995))+float(np.float32(-.0549999997)), 0)
        address = np.minimum(gamma, 1); blue = address[2]*31; grid = np.floor(blue)
        uv = (address[:2]*31+.5)*[1/1024,1/32]; uv[0] += grid/32
        low = sample(uv); high = sample(uv+[1/32,0]); graded = low+(high-low)*(blue-grid)
        gamma = gamma + (graded-gamma)*strength
        return gamma*(gamma*(gamma*float(np.float32(.305306017))+float(np.float32(.682171106)))+float(np.float32(.0125228781)))
    def independent(rgb, strength):
        # Direct trilinear cube lookup, independent of the shader's atlas UV addressing.
        gamma = np.maximum(np.maximum(rgb,0)**float(np.float32(.416666657))*float(np.float32(1.05499995))+float(np.float32(-.0549999997)),0)
        xyz = np.minimum(gamma,1)*31; low=np.floor(xyz).astype(int); frac=xyz-low; total=np.zeros(3)
        for z in (0,1):
            for y in (0,1):
                for x in (0,1):
                    address = np.minimum(low+[x,y,z],31)
                    total += pixels[address[1],address[2]*32+address[0]]*np.prod(np.where([x,y,z],frac,1-frac))
        mixed=gamma+(total-gamma)*strength
        return mixed*(mixed*(mixed*float(np.float32(.305306017))+float(np.float32(.682171106)))+float(np.float32(.0125228781)))
    random=np.random.default_rng(1032).uniform(0,1,(1024,3))
    tests=np.vstack((random, np.zeros(3), np.ones(3), [0,.5,1], [1,0,.5]))
    worst=0
    for rgb in tests:
        assert np.array_equal(folded(rgb,0),rgb)
        for strength in (.25, 1):
            error=float(np.max(np.abs(folded(rgb,strength)-independent(rgb,strength)))); worst=max(worst,error)
    assert worst < 1e-12, worst
    aces=lambda x: x*(2.50999999*x+.0299999993)/(x*(2.43000007*x+.589999974)+.140000001)
    samples=[]
    for value in (0,.01,.05,.18,.5,1,2,8,64,65504):
        mapped=aces(value); author=folded(np.repeat(mapped,3),1)
        assert np.isfinite(author).all()
        samples.append(dict(hdr=value, currentACES=mapped, restoredAuthorLinear=author.tolist(), originalBrightnessMinus40=(author*.6).tolist()))
    return dict(cases=len(tests)*3, exactDisabledIdentity=True, maxAbsoluteErrorVsIndependentCube=worst, samples=samples)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE, help='Unpatched accepted mobile BCG/ACES container')
    parser.add_argument('--output', type=Path, default=ROOT/'work/bcg_author_lut8')
    args=parser.parse_args()
    output=args.output.resolve()
    assert output.is_relative_to(ROOT/'work') and not output.exists() and not output.is_symlink()
    source_path=args.source.resolve(strict=True)
    assert not args.source.is_symlink()
    source=source_path.read_bytes(); before=sha(source); env=UnityPy.load(source)
    snapshot={i:sha(obj.get_raw_data()) for i,obj in env.file.objects.items()}; layout=container_layout(env.file)
    proof=author_proof(env); tests=numerical(env.file.objects[578].read().image)
    raw, code=patch(env.file.objects[838]); env.file.objects[838].set_raw_data(raw); result=env.file.save()
    check=UnityPy.load(result); assert container_layout(check.file)==layout and check.file.objects.keys()==snapshot.keys()
    for i,obj in check.file.objects.items():
        assert sha(obj.get_raw_data()) == (sha(raw) if i==838 else snapshot[i]),i
    check.file.reader.dispose(); env.file.reader.dispose()
    assert sha(source_path.read_bytes())==before
    output.mkdir(parents=True)
    vertex, fragment=split_stages(code.decode()); (output/'shader.vert').write_text(vertex); (output/'shader.frag').write_text(fragment)
    link=subprocess.run([str(VALIDATOR),'-l',str(output/'shader.vert'),str(output/'shader.frag')],capture_output=True,text=True)
    (output/'link.log').write_text(link.stdout+link.stderr); assert link.returncode==0, link.stdout+link.stderr
    (output/'sharedassets0.assets').write_bytes(result); (output/'sharedassets0.assets.838.shader').write_bytes(raw)
    receipt=dict(format='closing-shift-author-lut-bcg-v1',source=str(source_path),sourceSHA256=before,originalShader838SHA256=OLD,
        shader838SHA256=sha(raw),containerSHA256=sha(result),unrelatedRawObjectsPreserved=len(snapshot)-1,
        author=proof,numerical=tests,glslLinked=True,additionalTextureSamples=2,additionalPasses=0,
        brightnessContrastGammaSuffixByteExact=True,canonicalOrDeviceModified=False)
    (output/'manifest.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps(receipt,indent=2))

if __name__=='__main__':
    if not __debug__: raise RuntimeError('Strict shader assertions require Python without -O')
    main()
