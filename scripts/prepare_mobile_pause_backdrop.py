"""Replace only the two gameplay menu GrabPass materials with default UI.

Private OBB comparison on DIRECT23/COUNTER25/COORDS26. The Image sprites,
colors, alpha, masks, raycasts and layout remain authored. No world, Aura,
native, managed, shared shader or canonical application writes.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import struct
import subprocess

import UnityPy
from unity_shader_blob import read_programs

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/aura_direct23/level1'
OUTPUT = ROOT / 'work/pause_backdrop27/level1'
SHARED = ROOT / 'app/obb/assets/bin/Data/sharedassets0.assets'
UI = ROOT / 'app/assets/bin/Data/Managed/UnityEngine.UI.dll'
DNSPY = Path('C:/Portable/Total.Commander/Utilites/dnSpy/dnSpy.Console.exe')
BEFORE = '60d56700855bc8835d61b1297dc9e9bf293d7daa7704e255be7f535a9096bbf0'
MATERIAL_SHA = 'b0f880a2186dcca06c3f8b6d9d6e551ca22a0ba22cdd75571d4244a27b1846cf'
SHADER_SHA = '082edb61a7e9be572dabc08533a2899ae6344346830373f53e1c8e7aa736b3eb'
UI_SHA = '9ee880c1f004f1b1c29749645700cc5d9e484d25108db0e82ca920c51a7c06bc'
IMAGES = {
    38613: (7588, 17845944, '3e9922a7358240bd9d9dee26f8e0185449376a65e03df27b1062c08cf0077fe1'),
    39056: (8045, 17923064, 'b710b6320a0f1efa30ce4566ee34c1a78fcf1a33f367d9bd2b3820d98ca42ec4'),
}
OLD = struct.pack('<iq', 2, 115)
NEW = bytes(12)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def source_proof():
    env = UnityPy.load(SHARED.read_bytes())
    try:
        material, shader = env.file.objects[115], env.file.objects[814]
        require(material.type.name == 'Material' and sha(material.get_raw_data()) == MATERIAL_SHA,
                'Pinned MMBlur material changed')
        require(shader.type.name == 'Shader' and sha(shader.get_raw_data()) == SHADER_SHA,
                'Pinned UIBlur shader changed')
        mt, st = material.read_typetree(), shader.read_typetree()
        require(mt['m_Name'] == 'MMBlur' and mt['m_Shader'] == {'m_FileID': 0, 'm_PathID': 814},
                'Material-to-shader graph changed')
        require(st['m_ParsedForm']['m_Name'] == 'Custom/UIBlur' and st['platforms'] == [9],
                'Expected converted GLES UIBlur')
        subs = st['m_ParsedForm']['m_SubShaders']
        require(len(subs) == 1, 'Expected one UIBlur subshader')
        passes = subs[0]['m_Passes']
        require([p['m_Type'] for p in passes] == [2, 0, 2, 0]
                and all(passes[i]['m_TextureName'] == '' for i in (0, 2)),
                'Expected two anonymous GrabPasses and two draw passes')
        programs = read_programs(st, 9)
        require(len(programs) == 8 and all(b'_GrabTexture' in p['program_code']
                                         and b'texture(_GrabTexture,' in p['program_code'] for p in programs),
                'Connected GLES draws no longer sample grabbed framebuffer')
    finally:
        env.file.reader.dispose()
    require(sha(UI.read_bytes()) == UI_SHA, 'Pinned UGUI binary changed')
    expectations = {
        '0x060000CA': ('return this.defaultMaterial;', 'return this.m_Material;'),
        '0x060000C9': ('return Graphic.defaultGraphicMaterial;',),
        '0x060000B3': ('Canvas.GetDefaultCanvasMaterial()',),
    }
    for token, anchors in expectations.items():
        result = subprocess.run([str(DNSPY), '--md', token, str(UI)], capture_output=True,
                                text=True, check=True, timeout=30)
        require(all(anchor in result.stdout for anchor in anchors),
                'UGUI null-material fallback changed: ' + token)
    return {'material': 'sharedassets0.assets:115 MMBlur', 'material_sha256': MATERIAL_SHA,
            'shader': 'sharedassets0.assets:814 Custom/UIBlur', 'shader_sha256': SHADER_SHA,
            'pass_types': [2, 0, 2, 0], 'anonymous_grab_passes': 2, 'gles_programs_sampling_grab': 8,
            'default_ui_chain': list(expectations), 'ui_dll_sha256': UI_SHA,
            'official_grabpass': 'https://docs.unity3d.com/2020.3/Documentation/Manual/SL-GrabPass.html'}


def prepare(raw):
    require(len(raw) == 17965320 and sha(raw) == BEFORE, 'Exact DIRECT23 level1 preimage required')
    env = UnityPy.load(raw)
    try:
        require(len(env.file.objects) == 39299 and env.file.externals[1].path == 'sharedassets0.assets',
                'Pinned scene identity changed')
        candidate = bytearray(raw)
        for pid, (go, start, digest) in IMAGES.items():
            obj = env.file.objects[pid]
            data = obj.get_raw_data()
            require(obj.type.name == 'MonoBehaviour' and obj.byte_size == 136
                    and obj.byte_start == start and sha(data) == digest, 'Pinned Image changed: ' + str(pid))
            tree = obj.read_typetree(check_read=False)
            require(tree['m_GameObject'] == {'m_FileID': 0, 'm_PathID': go}
                    and tree['m_Script'] == {'m_FileID': 1, 'm_PathID': 2582}
                    and tree['m_Enabled'] == 1, 'Image owner/script/enabled changed')
            require(data[32:44] == OLD and raw[start + 32:start + 44] == OLD,
                    'Expected Image.m_Material MMBlur reference')
            candidate[start + 32:start + 44] = NEW
        return bytes(candidate)
    finally:
        env.file.reader.dispose()


def verify(before, after):
    require(sha(before) == BEFORE, 'Exact verification preimage required')
    require(len(after) == len(before), 'Container size changed')
    original, candidate = UnityPy.load(before), UnityPy.load(after)
    try:
        require(original.file.objects.keys() == candidate.file.objects.keys(), 'Object IDs changed')
        rows = []
        for pid, obj in original.file.objects.items():
            other = candidate.file.objects[pid]
            require((obj.type, obj.byte_start, obj.byte_size) == (other.type, other.byte_start, other.byte_size),
                    'Object type/position/size changed')
            old, new = obj.get_raw_data(), other.get_raw_data()
            expected = old[:32] + NEW + old[44:] if pid in IMAGES else old
            require(new == expected, 'Unexpected raw object change: ' + str(pid))
            if pid in IMAGES:
                rows.append({'path_id': pid, 'absolute_offset': obj.byte_start + 32,
                             'relative_offset': 32, 'old_material': [2, 115], 'new_material': [0, 0],
                             'raw_before_sha256': sha(old), 'raw_after_sha256': sha(new),
                             'color_rgba_preserved': list(struct.unpack_from('<ffff', old, 44))})
        inverse = bytearray(after)
        for _, start, _ in IMAGES.values():
            inverse[start + 32:start + 44] = OLD
        require(bytes(inverse) == before, 'Full inverse differs outside the two material references')
        return {'other_raw_objects_unchanged': len(original.file.objects) - 2,
                'changed_bytes': sum(a != b for a, b in zip(before, after)),
                'full_container_inverse_exact': True, 'colors_sprites_layout_raycast_preserved': True,
                'objects': rows}
    finally:
        original.file.reader.dispose()
        candidate.file.reader.dispose()


def self_test(before, after):
    passed = []
    def reject(name, action, expected):
        try:
            action()
        except ValueError as error:
            require(expected in str(error), 'Unexpected guard result: ' + name)
            passed.append(name)
            return
        raise AssertionError('Invalid input accepted: ' + name)
    reject('wrong_preimage', lambda: prepare(before[:-1]), 'preimage')
    reject('already_patched', lambda: prepare(after), 'preimage')
    reject('unchanged_material', lambda: verify(before, before), 'raw object')
    def mutate(offset):
        data = bytearray(after)
        data[offset] ^= 1
        return bytes(data)
    reject('changed_authored_color', lambda: verify(before, mutate(IMAGES[38613][1] + 44)), 'raw object')
    reject('wrong_material_target', lambda: verify(before, mutate(IMAGES[39056][1] + 36)), 'raw object')
    reject('changed_other_object', lambda: verify(before, mutate(17965319)), 'raw object')
    return passed


def main():
    before = SOURCE.read_bytes()
    proof = source_proof()
    after = prepare(before)
    checks = verify(before, after)
    tests = self_test(before, after)
    require(OUTPUT.resolve() == OUTPUT.absolute() and not OUTPUT.exists(), 'Fresh private output required')
    require(SOURCE.read_bytes() == before, 'Retained input changed concurrently')
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT.open('xb') as stream:
        stream.write(after)
    verify(before, OUTPUT.read_bytes())
    manifest = {'input': str(SOURCE), 'output': str(OUTPUT), 'source_sha256': BEFORE,
                'output_sha256': sha(after), 'script_sha256': sha(Path(__file__).read_bytes()),
                'source_proof': proof, 'checks': checks, 'negative_guards': tests,
                'status': 'isolated two-backdrop GrabPass bypass; device acceptance pending',
                'canonical_device_managed_native_shader_modified': False}
    OUTPUT.with_name('manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'output': str(OUTPUT), 'sha256': sha(after), 'checks': checks, 'negative_guards': tests}, indent=2))


if __name__ == '__main__':
    main()
