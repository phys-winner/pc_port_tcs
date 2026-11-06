"""Isolate Aura shadow capture on DIRECT23; preserve all actual Unity lights.

Only AuraLight.useShadow changes. It prevents Aura's capture/array collection
and consequently its volumetric shadow sampling. It does not disable the real
Light's surface shadows, lighting, cookies, or alter the compute experiment.
"""
from __future__ import annotations

import hashlib
import json
import struct
import subprocess
from pathlib import Path

import UnityPy


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/aura_direct23/level1'
OUTPUT = ROOT / 'work/aura_shadow24/level1'
AURA = ROOT / 'app/assets/bin/Data/Managed/Aura2_Core.dll'
DNSPY = Path('C:/Portable/Total.Commander/Utilites/dnSpy/dnSpy.Console.exe')
BEFORE = '60d56700855bc8835d61b1297dc9e9bf293d7daa7704e255be7f535a9096bbf0'
AFTER = 'd576e62fab4a60d7c8baed39d9fd18b4d6b0ba52a420cd168ea6936885db0026'
AURA_SHA = '7c0c666f5eabce1c46be3c476ce8f1c7e4dd2f1774af8287f39feb67ea22aa50'
IDS = tuple(range(39230, 39294))
FIELD_OFFSET = 76
SCRIPT = {'m_FileID': 1, 'm_PathID': 2439}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def block(text, anchor, start=0):
    """Extract a balanced block from these pinned decompiles."""
    begin = text.index('{', text.index(anchor, start))
    depth = 0
    for end in range(begin, len(text)):
        depth += (text[end] == '{') - (text[end] == '}')
        if depth == 0:
            return text[begin:end + 1]
    raise ValueError('Unbalanced pinned source block: ' + anchor)


def source_proof():
    require(sha(AURA.read_bytes()) == AURA_SHA and DNSPY.is_file(), 'Pinned Aura DLL and local dnSpy required')
    text = {}
    for name in ('AuraLight', 'LightsCommonDataManager', 'Texture2DArrayComposer'):
        result = subprocess.run([str(DNSPY), '-t', 'Aura2API.' + name, str(AURA)],
                                capture_output=True, text=True, timeout=30, check=True)
        require('class ' + name in result.stdout, 'Missing pinned type: ' + name)
        text[name] = result.stdout
    light = text['AuraLight']
    require('public bool useShadow = true;' in light, 'Public authored field changed')
    casts = block(light, 'public bool CastsShadows')
    require('return QualitySettings.shadows != ShadowQuality.Disable && this.LightComponent.shadows != LightShadows.None && this.useShadow;' in casts,
            'CastsShadows predicate changed')
    initialize = block(light, 'private void Initialize()')
    capture = block(initialize, 'if (this.CastsShadows)')
    require(initialize.count('.AddCommandBuffer(') == capture.count('.AddCommandBuffer(') == 2
            and 'LightEvent.AfterShadowMap' in capture and 'LightEvent.BeforeScreenspaceMask' in capture,
            'Shadow command-buffer creation escaped the CastsShadows gate')
    register = block(text['LightsCommonDataManager'], 'public void RegisterLight(')
    anchor = 'if (auraLight.CastsShadows)'
    require(register.count(anchor) == 3, 'Expected spot/directional/point shadow registration gates')
    gated = []
    start = 0
    for _ in range(3):
        position = register.index(anchor, start)
        gated.append(block(register, anchor, position))
        start = position + len(anchor)
    require(register.count('.AddTexture(auraLight.shadowMapRenderTexture)') == 3
            and sum(g.count('.AddTexture(auraLight.shadowMapRenderTexture)') for g in gated) == 3,
            'Shadow collector registration escaped the CastsShadows gate')
    generate = block(text['Texture2DArrayComposer'], 'public void Generate()')
    nonempty = block(generate, 'if (this._texturesList.Count > 0)')
    require(generate.count('Graphics.CopyTexture(') == nonempty.count('Graphics.CopyTexture(') == 1,
            'Collector copy must require a registered shadow texture')
    return {
        'dll_sha256': AURA_SHA, 'field': 'Aura2API.AuraLight.useShadow', 'field_token': '0x04000025',
        'CastsShadows_method': '0x06000025', 'Initialize_method': '0x0600002f',
        'RegisterLight_method': '0x060000d3', 'Generate_method': '0x06000120',
        'predicate': 'Unity shadow quality && Light.shadows && AuraLight.useShadow',
        'capture_commands_gated': ['AfterShadowMap', 'BeforeScreenspaceMask'],
        'collector_registration_gated': ['spot', 'directional', 'point'],
        'collector_copy_requires_nonempty_list': True,
        'quality_sampling_flags_alone_do_not_disable_capture': True,
        'cold_serialized_change_avoids_live_directional_unregister_ordering': True,
    }


def prepare(raw):
    require(sha(raw) == BEFORE, 'Exact DIRECT23 level1 preimage required')
    env = UnityPy.load(raw)
    try:
        require(len(env.file.objects) == 39299 and env.file.externals[0].path == 'globalgamemanagers.assets',
                'Pinned scene container identity changed')
        found = tuple(sorted(o.path_id for o in env.objects if o.type.name == 'MonoBehaviour'
                             and o.read_typetree(check_read=False)['m_Script'] == SCRIPT))
        require(found == IDS, 'Expected exactly the 64 authored AuraLight components')
        candidate = bytearray(raw)
        rows = []
        for pid in IDS:
            obj = env.file.objects[pid]
            old = obj.get_raw_data()
            require(len(old) == 148 and old[76:80] == b'\x01\0\0\0', 'Unexpected useShadow layout: ' + str(pid))
            require(old[80:84] == b'\x01\0\0\0' and struct.unpack_from('<i', old, 12)[0] in (0, 1),
                    'Unexpected authored cookie/enabled baseline: ' + str(pid))
            offset = obj.byte_start + FIELD_OFFSET
            require(raw[offset:offset + 4] == b'\x01\0\0\0', 'Absolute flag position mismatch')
            candidate[offset] = 0
            rows.append({'path_id': pid, 'absolute_offset': offset, 'relative_offset': FIELD_OFFSET,
                         'enabled_preserved': old[12], 'cookie_preserved': old[80], 'before_sha256': sha(old)})
        result = bytes(candidate)
        require(sha(result) == AFTER, 'Unexpected SHADOW24 postimage')
        inverse = bytearray(result)
        for row in rows:
            inverse[row['absolute_offset']] = 1
        require(bytes(inverse) == raw, 'Full-container inverse must reconstruct DIRECT23 exactly')
        return result, rows
    finally:
        env.file.reader.dispose()


def verify(before, after, rows):
    original, candidate = UnityPy.load(before), UnityPy.load(after)
    try:
        require(original.file.objects.keys() == candidate.file.objects.keys(), 'Serialized object IDs changed')
        row_by_id = {row['path_id']: row for row in rows}
        lights = 0
        for pid, obj in original.file.objects.items():
            other = candidate.file.objects[pid]
            require((obj.type, obj.byte_start, obj.byte_size) == (other.type, other.byte_start, other.byte_size),
                    'Serialized type/position/size changed: ' + str(pid))
            old, new = obj.get_raw_data(), other.get_raw_data()
            expected = old[:76] + b'\0' + old[77:] if pid in row_by_id else old
            require(new == expected, 'Unexpected raw object change: ' + str(pid))
            if pid in row_by_id:
                row_by_id[pid]['after_sha256'] = sha(new)
            if obj.type.name == 'Light':
                lights += 1
                require(old == new, 'A real Unity Light changed')
        require(lights == 107, 'Expected all107 real Unity Light objects')
        return {'all_other_raw_objects_unchanged': len(original.file.objects) - len(IDS),
                'unity_light_objects_unchanged': lights, 'quality_36456_raw_unchanged': True,
                'original_disabled_components_preserved': [r['path_id'] for r in rows if not r['enabled_preserved']],
                'changed_bytes': len(rows), 'full_container_inverse_exact': True}
    finally:
        original.file.reader.dispose()
        candidate.file.reader.dispose()


def main():
    proof = source_proof()
    before = SOURCE.read_bytes()
    after, rows = prepare(before)
    checks = verify(before, after, rows)
    require(OUTPUT.resolve() == OUTPUT.absolute(), 'Private output may not redirect through a link')
    require(not OUTPUT.exists() or OUTPUT.read_bytes() == after, 'Refusing to replace a different private level1')
    manifest = {'input': str(SOURCE), 'output': str(OUTPUT), 'source_sha256': BEFORE, 'output_sha256': AFTER,
                'script_sha256': sha(Path(__file__).read_bytes()), 'source_proof': proof, 'checks': checks,
                'objects': rows, 'status': 'isolated shadow capture candidate; hardware result untested'}
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(after)
    require(OUTPUT.read_bytes() == after and SOURCE.read_bytes() == before, 'Output reread or retained input changed')
    OUTPUT.with_name('manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'output': str(OUTPUT), 'sha256': AFTER, 'checks': checks}, indent=2))


if __name__ == '__main__':
    main()
