"""Prepare isolated work-only Aura quality candidates from the accepted rev15 level1.

linear-only changes Cubic to Linear. linear-grid64 additionally changes the grid
from 80x48x96 to 64x40x64, so comparison against linear-only isolates grid cost.
All lights, masks, density, far distance, temporal/occlusion flags and other
objects remain exact. No shader/DLL edit, apply option, or device operation.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct

import UnityPy

from prepare_depth_precision import container_layout

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/a64-mobile-revision15/app/obb/assets/bin/Data/level1'
CONTAINER = 'c1e4f862205276420b050490df9e1851ce132224011f67eb0ddcfc498d7233c1'
OBJECT_ID = 36456
PREIMAGE = 'd10e5288654d8b70e1b77a2ad800ad0bf99f256f94e7f41f730eb8ad8b9ce2d3'
CAMERA = 'a56ce3da6041f5c58bc90c6fde3ddb0821577ae23144cdfd92df0f053cea33fb'
BASE = 'c473f0e2a9ba9faa019013fb1187d9907474458760186641a34fad27a67a64f3'
MODES = {'linear-only': (80, 48, 96), 'linear-grid64': (64, 40, 64)}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def patch(raw, mode):
    assert mode in MODES
    assert len(raw) == 196 and sha(raw) == PREIMAGE, 'Expected exact accepted AuraQualitySettings36456'
    assert struct.unpack_from('<iq', raw, 16) == (1, 1350)
    assert struct.unpack_from('<i', raw, 28)[0] == 29
    assert raw[32:61] == b'Default Aura Quality Settings'
    assert struct.unpack_from('<3i', raw, 68) == (80, 48, 96)
    assert struct.unpack_from('<i', raw, 144)[0] == 1, 'Expected Cubic enum1'
    result = bytearray(raw)
    struct.pack_into('<i', result, 144, 0)
    if mode == 'linear-grid64': struct.pack_into('<3i', result, 68, *MODES[mode])
    allowed = set(range(144, 148)) | (set(range(68, 80)) if mode == 'linear-grid64' else set())
    assert all(old == new for i, (old, new) in enumerate(zip(raw, result)) if i not in allowed)
    reverse = bytearray(result)
    struct.pack_into('<i', reverse, 144, 1)
    struct.pack_into('<3i', reverse, 68, 80, 48, 96)
    assert bytes(reverse) == raw
    assert struct.unpack_from('<3i', result, 68) == MODES[mode]
    assert struct.unpack_from('<i', result, 144)[0] == 0
    return bytes(result)


def self_test(raw):
    linear = patch(raw, 'linear-only')
    small = patch(raw, 'linear-grid64')
    assert linear[:144] == raw[:144] and linear[148:] == raw[148:]
    assert small[:68] == linear[:68] and small[80:] == linear[80:]
    # Refuse an already changed input or any drift in unrelated authored data.
    malformed = bytearray(raw); malformed[84] ^= 1
    rejected = 0
    for invalid in (linear, small, bytes(malformed), raw[:-1]):
        try:
            patch(invalid, 'linear-only')
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('Unexpected preimage accepted')
    assert rejected == 4
    for grid in MODES.values(): assert all(n > 0 and n % size == 0 for n, size in zip(grid, (8, 8, 2)))
    return dict(exactFieldDelta=True, gridComparisonChangesGridOnly=True,
                invalidPreimagesRejected=rejected, gridsMatchExistingComputeGroups=True)


def prepare(source, mode):
    env = UnityPy.load(source)
    try:
        snapshot = {pid: sha(obj.get_raw_data()) for pid, obj in env.file.objects.items()}
        layout = container_layout(env.file)
        obj = env.file.objects[OBJECT_ID]
        assert obj.type.name == 'MonoBehaviour'
        camera = env.file.objects[39229].get_raw_data()
        assert sha(camera) == CAMERA and struct.unpack_from('<iq', camera, 44) == (0, OBJECT_ID)
        assert struct.unpack_from('<iq', camera, 32) == (0, 36701)
        assert sha(env.file.objects[36701].get_raw_data()) == BASE
        raw = obj.get_raw_data()
        tests = self_test(raw)
        candidate = patch(raw, mode)
        obj.set_raw_data(candidate)
        packed = env.file.save()
        check = UnityPy.load(packed)
        try:
            assert container_layout(check.file) == layout
            assert check.file.objects.keys() == snapshot.keys()
            for pid, item in check.file.objects.items():
                assert sha(item.get_raw_data()) == (sha(candidate) if pid == OBJECT_ID else snapshot[pid]), pid
        finally:
            check.file.reader.dispose()
    finally:
        env.file.reader.dispose()
    cells = MODES[mode][0] * MODES[mode][1] * MODES[mode][2]
    receipt = dict(format='closing-shift-aura-quality-candidate-v1', mode=mode,
                   sourceContainerSHA256=CONTAINER, objectId=OBJECT_ID, preimageSHA256=PREIMAGE,
                   rawSHA256=sha(candidate), containerSHA256=sha(packed),
                   ownerCameraObject=39229, ownerGameObject=3151, qualityScriptPPtr=[1, 1350],
                   gridBefore=[80, 48, 96], gridAfter=MODES[mode], gridOffset=68,
                   filteringBefore='Cubic1', filteringAfter='Linear0', filteringOffset=144,
                   gridCells=cells, logicalVolumeAndAppendBytes=cells*64,
                   untouchedRawObjects=len(snapshot)-1, containerLayoutPreserved=True,
                   allOtherQualityBytesPreserved=True, baseDensityAndLightsAndMasksPreserved=True,
                   temporalAndOcclusionFlagsPreserved=True, tests=tests,
                   deviceAcceptance=False, canonicalOrDeviceModified=False)
    return candidate, packed, receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=ROOT / 'work/mobile_aura16')
    parser.add_argument('--mode', choices=(*MODES, 'all'), default='all')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    path = args.source.resolve(strict=True)
    assert not args.source.is_symlink()
    source = path.read_bytes()
    assert sha(source) == CONTAINER, 'Expected exact accepted revision15 level1 container'
    if args.self_test:
        env = UnityPy.load(source)
        try: print(json.dumps(self_test(env.file.objects[OBJECT_ID].get_raw_data()), indent=2))
        finally: env.file.reader.dispose()
        return
    output = args.output.resolve()
    assert output.is_relative_to(ROOT / 'work') and output != ROOT / 'work'
    assert not output.exists() and not args.output.is_symlink(), 'Use a fresh work output directory'
    modes = list(MODES) if args.mode == 'all' else [args.mode]
    results = {mode: prepare(source, mode) for mode in modes}
    assert path.read_bytes() == source, 'Input changed concurrently'
    output.mkdir(parents=True)
    receipts = []
    for mode, (raw, packed, receipt) in results.items():
        folder = output / mode; folder.mkdir()
        (folder / 'level1_36456.raw').write_bytes(raw)
        (folder / 'level1').write_bytes(packed)
        assert sha((folder / 'level1').read_bytes()) == receipt['containerSHA256']
        (folder / 'manifest.json').write_text(json.dumps(receipt, indent=2) + '\n')
        receipts.append(receipt)
    (output / 'manifest.json').write_text(json.dumps(receipts, indent=2) + '\n')
    print(json.dumps(receipts, indent=2))


if __name__ == '__main__':
    if not __debug__: raise RuntimeError('Strict asset assertions require Python without -O')
    main()
