"""Restore the exact Unity Android external-texture video passes; keep desktop passes."""
from pathlib import Path
import os
import argparse
import copy
import hashlib
import json
import subprocess
import UnityPy
import lz4.block
from convert_shaders import encode_segments, STAGES
from unity_shader_blob import read_programs, encode_record
from prepare_sampler_locations import split_stages

ROOT = Path(__file__).resolve().parents[1]
VALIDATOR = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/emulator/lib64/vulkan/glslangValidator.exe'
SOURCE = ROOT / 'work/tools/unity_builtin_shaders_2020.3.30f1/source/DefaultResourcesExtra/VideoDecodeAndroid.shader'
NAMES = ('RGBAExternal_To_RGBA', 'RGBASplitExternal_To_RGBA')
PATH_ID = 16002


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def build_shader(original, output):
    assert sha(SOURCE.read_bytes()) == 'dd66471dc66a704e1fb68293e137d66a3179aa4e3f14b18aeef2a6eec94bfe4f'
    official = SOURCE.read_text(encoding='utf-8-sig')
    assert all('Name "' + name + '"' in official for name in NAMES)
    assert 'samplerExternalOES _MainTex' in official and 'textureCoordSplit.yz).g' in official
    tree = copy.deepcopy(original)
    tree['m_ParsedForm']['m_Name'] = 'Hidden/VideoDecodeAndroid'
    tree['m_ParsedForm']['m_PropInfo']['m_Props'] = [copy.deepcopy(original['m_ParsedForm']['m_PropInfo']['m_Props'][0])]
    template = original['m_ParsedForm']['m_SubShaders'][0]['m_Passes'][0]
    records = read_programs(original, 9)
    vertex, _ = split_stages(records[0]['program_code'].decode())
    assert '0.305306' in records[1]['program_code'].decode(), 'Expected source linear color-space variant'
    assert template['progVertex']['m_SubPrograms'][3]['m_LocalKeywordIndices'] == [6]
    tail = copy.deepcopy(records[0]['tail'])
    tail['resources'] = [r for r in tail['resources'] if r['name'] == '_MainTex']
    assert len(tail['resources']) == 1 and tail['resources'][0]['index'] == 0
    passes, binary = [], []
    for split, name in enumerate(NAMES):
        p = copy.deepcopy(template)
        p['m_State']['m_Name'] = name
        assert [p['m_State'][key]['val'] for key in ('zTest', 'zWrite', 'culling')] == [8.0, 0.0, 0.0]
        p['m_Platforms'] = [9]
        for stage in STAGES:
            p[stage]['m_SubPrograms'] = []
        vs = vertex
        if split:
            old = 'vs_TEXCOORD0.xy = in_TEXCOORD0.xy * _MainTex_ST.xy + _MainTex_ST.zw;'
            assert vs.count(old) == 1
            vs = vs.replace('out highp vec2 vs_TEXCOORD0;', 'out highp vec3 vs_TEXCOORD0;').replace(old,
                'vs_TEXCOORD0.xz = vec2(0.5 * in_TEXCOORD0.x * _MainTex_ST.x, in_TEXCOORD0.y * _MainTex_ST.y) + _MainTex_ST.zw;\n'
                'vs_TEXCOORD0.y = vs_TEXCOORD0.x + 0.5 * _MainTex_ST.x;')
        for adjust in (False, True):
            fragment = '\n'.join([
                '#version 310 es', '#extension GL_OES_EGL_image_external_essl3 : require',
                'precision highp float;', 'precision highp int;',
                'layout(location = 0) uniform highp samplerExternalOES _MainTex;',
                'in highp vec' + ('3' if split else '2') + ' vs_TEXCOORD0;',
                'layout(location = 0) out highp vec4 SV_Target0;',
                'vec4 AdjustForColorSpace(vec4 color) {',
                ('vec3 sRGB = color.rgb; return vec4(sRGB * (sRGB * (sRGB * 0.305306011 + 0.682171111) + 0.012522878), color.a);'
                 if adjust else 'return color;'), '}', 'void main() {',
                ('SV_Target0.rgb = AdjustForColorSpace(texture(_MainTex, vs_TEXCOORD0.xz)).rgb;\n'
                 'SV_Target0.a = texture(_MainTex, vs_TEXCOORD0.yz).g;'
                 if split else 'SV_Target0 = AdjustForColorSpace(texture(_MainTex, vs_TEXCOORD0.xy));'), '}', ''])
            folder = output / ('split' if split else 'rgba') / str(int(adjust))
            folder.mkdir(parents=True, exist_ok=True)
            v, f = folder / 'video.vert', folder / 'video.frag'
            v.write_text(vs); f.write_text(fragment)
            checked = subprocess.run([str(VALIDATOR), '-l', str(v), str(f)], capture_output=True, text=True)
            (folder / 'link.log').write_text(checked.stdout + checked.stderr)
            assert checked.returncode == 0, checked.stdout + checked.stderr
            code = '#ifdef VERTEX\n' + vs + '#endif\n#ifdef FRAGMENT\n' + fragment + '#endif\n'
            record = {'version': 201806140, 'program_type': 4, 'stats': [0, 0, 0, 0], 'keywords': [],
                'local_keywords': ['ADJUST_TO_LINEARSPACE'] if adjust else [], 'program_code': code.encode(), 'tail': tail}
            index = len(binary)
            binary.append(encode_record(record))
            for tier in range(3):
                sp = copy.deepcopy(template['progVertex']['m_SubPrograms'][3 if adjust else 0])
                sp.update(m_BlobIndex=index, m_ShaderHardwareTier=tier)
                p['progVertex']['m_SubPrograms'].append(sp)
        h = hashlib.md5(b''.join(binary[-2:])).digest()
        p['m_EditorDataHash'] = [{f'bytes[{i}]': value for i, value in enumerate(h)}]
        passes.append(p)
    tree['m_ParsedForm']['m_SubShaders'] = [copy.deepcopy(original['m_ParsedForm']['m_SubShaders'][0])]
    tree['m_ParsedForm']['m_SubShaders'][0]['m_Passes'] = passes
    segments = encode_segments(binary)
    packed, offsets, lengths = bytearray(), [], []
    for segment in segments:
        compressed = lz4.block.compress(segment, store_size=False)
        offsets.append(len(packed)); lengths.append(len(compressed)); packed.extend(compressed)
    tree.update(platforms=[9], offsets=[offsets], compressedLengths=[lengths],
        decompressedLengths=[[len(s) for s in segments]], compressedBlob=list(packed))
    assert [r['record_bytes'] for r in read_programs(tree, 9)] == binary
    return tree


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    output = ROOT / 'work/android_video_shader'
    output.mkdir(exist_ok=True)
    extra = ROOT / 'app/assets/bin/Data/Resources/unity_builtin_extra'
    global_path = ROOT / 'app/assets/bin/Data/globalgamemanagers'
    inputs = {p: p.read_bytes() for p in (extra, global_path)}
    env = UnityPy.load(inputs[extra])
    before = {o.path_id: sha(o.get_raw_data()) for o in env.objects}
    template = env.file.objects[16001]
    assert sha(template.get_raw_data()) == '82e9e2f0a57e9c993848bafabc4b7387a2b7c482d581206149e092630756fa69'
    tree = build_shader(template.read_typetree(), output)
    if PATH_ID in before:
        assert env.file.objects[PATH_ID].read_typetree() == tree, 'Android video shader ID collision or changed candidate'
    else:
        obj = copy.copy(template)
        obj.path_id = PATH_ID
        obj.save_typetree(tree)
        env.file.objects[PATH_ID] = obj
    serialized = env.file.save()
    saved = {o.path_id: o for o in UnityPy.load(serialized).objects}
    assert set(saved) == set(before) | {PATH_ID}
    assert all(sha(saved[key].get_raw_data()) == value for key, value in before.items())
    assert saved[PATH_ID].read_typetree() == tree
    game = UnityPy.load(inputs[global_path])
    game_before = {o.path_id: sha(o.get_raw_data()) for o in game.objects}
    graphics = next(o for o in game.objects if o.type.name == 'GraphicsSettings')
    settings = graphics.read_typetree()
    assert game.file.externals[1].path == 'Resources/unity_builtin_extra'
    assert next(o for o in game.objects if o.type.name == 'PlayerSettings').read_typetree()['m_ActiveColorSpace'] == 1
    pointer = {'m_FileID': 2, 'm_PathID': PATH_ID}
    if pointer not in settings['m_AlwaysIncludedShaders']:
        settings['m_AlwaysIncludedShaders'].append(pointer)
    graphics.save_typetree(settings)
    mapper = next(o for o in game.objects if o.type.name == 'ScriptMapper')
    mapping = mapper.read_typetree()
    entries = mapping['m_Shaders']['m_ObjectToName']
    matching = [i for i, (ptr, name) in enumerate(entries) if name == tree['m_ParsedForm']['m_Name']]
    assert len(matching) == 1
    index = matching[0]
    assert entries[index][0] in ({'m_FileID': 0, 'm_PathID': 0}, pointer)
    entries[index] = (pointer, tree['m_ParsedForm']['m_Name'])
    mapper.save_typetree(mapping)
    globals_saved = game.file.save()
    restored = {o.path_id: o for o in UnityPy.load(globals_saved).objects}
    assert set(restored) == set(game_before)
    assert all(sha(restored[key].get_raw_data()) == value for key, value in game_before.items() if key not in (graphics.path_id, mapper.path_id))
    assert restored[graphics.path_id].read_typetree() == settings
    assert restored[mapper.path_id].read_typetree() == mapping
    outputs = {extra: serialized, global_path: globals_saved}
    manifest = {'source': str(SOURCE), 'sourceSha256': sha(SOURCE.read_bytes()), 'pathId': PATH_ID,
        'name': tree['m_ParsedForm']['m_Name'], 'passes': list(NAMES), 'linkedVariants': 4,
        'allOriginalObjectsPreserved': True, 'deviceValidated': False, 'applied': args.apply,
        'files': [{'path': str(p), 'beforeSha256': sha(inputs[p]), 'afterSha256': sha(raw)} for p, raw in outputs.items()]}
    for path, raw in outputs.items():
        (output / path.name).write_bytes(raw)
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2))
    if args.apply:
        assert all(path.read_bytes() == raw for path, raw in inputs.items()), 'Concurrent canonical change'
        for path, raw in outputs.items():
            temporary = path.with_name(path.name + '.android-video.tmp')
            temporary.write_bytes(raw); temporary.replace(path)
        license_source = SOURCE.parents[1] / 'license.txt'
        license_target = ROOT / 'app/assets/licenses/UnityBuiltinShaders-2020.3.30f1.txt'
        assert not license_target.exists() or license_target.read_bytes() == license_source.read_bytes()
        license_target.parent.mkdir(parents=True, exist_ok=True)
        license_target.write_bytes(license_source.read_bytes())
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
