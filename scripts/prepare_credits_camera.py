"""Stop the hidden world camera when the final, overlay-only credits start."""
from pathlib import Path
import argparse
import hashlib
import json
import struct

import UnityPy

ROOT = Path(__file__).resolve().parents[1]
SCENE_SHA = 'b0b7cc58d928811414ff0d0cde8422b70cf6fa93ff8524a160bea35c38420a1e'
SEQUENCE_SHA = '8fe42990e14d3dc9f170da2ea10ef4c2f5237880894107a5df23751afad9f67e'


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def string(value):
    raw = value.encode('utf-8')
    return struct.pack('<i', len(raw)) + raw + bytes((-len(raw)) % 4)


def sequence(raw):
    assert len(raw) == 304 and sha(raw) == SEQUENCE_SHA, 'Unreviewed final credits sequence'
    assert struct.unpack_from('<if', raw, 56) == (1, 108.0)
    assert raw[212:284] == bytes(72), 'Authored event extras changed'
    # The cutscene reactivates the camera object; disable its renderer while keeping the listener.
    call = (struct.pack('<iiq', 1, 0, 21673) + string('UnityEngine.Camera, UnityEngine.CoreModule')
            + string('set_enabled') + struct.pack('<iiq', 6, 0, 0)
            + string('UnityEngine.Object, UnityEngine') + struct.pack('<if', 0, 0)
            + string('') + struct.pack('<ii', 0, 2))
    first = struct.pack('<f', 0) + call + raw[212:284]
    header = bytearray(raw[:60])
    struct.pack_into('<i', header, 56, 2)
    result = bytes(header) + first + raw[60:]
    restored = bytearray(result[:60] + result[60 + len(first):])
    struct.pack_into('<i', restored, 56, 1)
    assert bytes(restored) == raw, 'Existing timer, menu event or sequence flags changed'
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scene', type=Path, default=ROOT / 'app/obb/assets/bin/Data/level1')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    source = args.scene.resolve(strict=True)
    output = args.output.resolve()
    assert source.is_relative_to(ROOT / 'app') or source.is_relative_to(ROOT / 'work')
    assert output.is_relative_to(ROOT / 'work') and not output.exists() and not source.is_relative_to(output)
    original = source.read_bytes()
    assert sha(original) == SCENE_SHA, 'Unreviewed scene preimage'
    env = UnityPy.load(original)
    before = {pid: sha(obj.get_raw_data()) for pid, obj in env.file.objects.items()}
    camera = env.file.objects[21673].read()
    assert camera.m_GameObject.path_id == 3151 and camera.m_Enabled
    assert env.file.objects[3151].read().m_Name == 'MainCamera'
    transforms = {o.path_id: o.read() for o in env.file.objects.values() if o.type.name in ('Transform', 'RectTransform')}
    current = 36097
    ancestors = []
    while current:
        transform = transforms[current]
        assert transform.m_GameObject.path_id != 3151, 'Credits are children of the world camera'
        go = env.file.objects[transform.m_GameObject.path_id].read()
        for component in go.m_Component:
            obj = env.file.objects[component.component.path_id]
            if obj.type.name == 'Canvas':
                tree = obj.read_typetree()
                ancestors.append({'go': go.m_Name, 'renderMode': tree['m_RenderMode']})
        current = transform.m_Father.path_id
    assert ancestors and all(row['renderMode'] == 0 for row in ancestors), 'Credits require an enabled world camera'
    obj = env.file.objects[37867]
    original_sequence = obj.get_raw_data()
    changed = sequence(original_sequence)
    for invalid in (b'corrupt', changed):
        try:
            sequence(invalid)
        except AssertionError:
            pass
        else:
            raise AssertionError('Unknown or already-patched sequence accepted')
    obj.set_raw_data(changed)
    raw = env.file.save()
    check = UnityPy.load(raw)
    assert check.file.objects.keys() == before.keys()
    assert all(sha(o.get_raw_data()) == (sha(changed) if pid == 37867 else before[pid]) for pid, o in check.file.objects.items())
    assert source.read_bytes() == original, 'Scene source changed during preparation'
    output.mkdir(parents=True)
    (output / 'level1').write_bytes(raw)
    report = {'status': 'CREDITS_CAMERA_HOST_PASS_DEVICE_OPEN', 'source': str(source), 'sourceSHA256': sha(original),
              'outputSHA256': sha(raw), 'bytes': len(raw), 'changedPathId': 37867,
              'unrelatedRawObjectsPreserved': len(before) - 1, 'existing108SecondMenuEventUnchanged': True,
              'firstEvent': {'seconds': 0, 'targetCamera': 21673, 'method': 'set_enabled', 'bool': False},
              'overlayCanvasAncestors': ancestors, 'reverseReplayAndPreimageGuards': True,
              'texturesVideoAudioAndNativeUnchanged': True}
    (output / 'manifest.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report), flush=True)


if __name__ == '__main__':
    main()
