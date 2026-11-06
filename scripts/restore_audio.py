"""Restore the shipping AudioManager and VideoPlayer audio fields, preserving all other objects."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
import tempfile

import UnityPy

ROOT = Path(__file__).resolve().parents[1]
ORIGINAL = Path('external/game/TheClosingShift_Data')
SOURCES = {
    'globalgamemanagers': ('assets/bin/Data/globalgamemanagers', 'b40cd5e104e504409640daf9905ce920b87c9d039fc5b7393b2e6d73b854d29e', 'AudioManager', 1),
    'level0': ('obb/assets/bin/Data/level0', '9cf742beea213ddae53ec6472fbfa9a078ce45217b82a12bdf24fc03e8dd3942', 'VideoPlayer', 11),
    'level1': ('obb/assets/bin/Data/level1', '5312a8fbfc1305f038f8ed9c5e1ef87416875b592efb3bbf5018f408ba8f9cb8', 'VideoPlayer', 11),
}


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def state(file):
    return file.unity_version, int(file.target_platform), [(e.path, e.guid.hex(), e.type) for e in file.externals]


def muted(tree, kind):
    result = copy.deepcopy(tree)
    if kind == 'AudioManager':
        result['m_Volume'] = 0.0
    else:
        result['m_AudioOutputMode'] = 0
        result['m_DirectAudioVolumes'] = [0.0] * len(result['m_DirectAudioVolumes'])
        result['m_DirectAudioMutes'] = [True] * len(result['m_DirectAudioMutes'])
    return result


def restore(app, commit):
    app = app.resolve(strict=True)
    require(app == ROOT / 'app' or app.is_relative_to(ROOT / 'work'), 'Target must be canonical app or staging inside work')
    outputs, reports = [], []
    for name, (relative, expected_source, kind, count) in SOURCES.items():
        original = (ORIGINAL / name).read_bytes()
        require(sha(original) == expected_source, 'Shipping source hash differs: ' + name)
        target = (app / relative).resolve(strict=True)
        require(target.is_relative_to(app), 'Target escaped app: ' + relative)
        before_raw = target.read_bytes()
        env, source = UnityPy.load(before_raw), UnityPy.load(original)
        try:
            identity = state(env.file)
            before = {pid: sha(obj.get_raw_data()) for pid, obj in env.file.objects.items()}
            originals = {obj.path_id: obj for obj in source.objects if obj.type.name == kind}
            actual = {obj.path_id for obj in env.objects if obj.type.name == kind}
            require(len(originals) == count and actual == originals.keys(), 'Audio object inventory changed: ' + name)
            changed, restored = [], {}
            for pid, source_obj in originals.items():
                obj = env.file.objects[pid]
                authored = source_obj.read_typetree()
                current = obj.read_typetree()
                require(current in (authored, muted(authored, kind)), 'Audio object is neither authored nor known muted preimage: ' + name + ':' + str(pid))
                # Restore raw shipping bytes only after proving the entire object differs solely by the known mute.
                raw = source_obj.get_raw_data()
                restored[pid] = sha(raw)
                if obj.get_raw_data() != raw:
                    obj.set_raw_data(raw)
                    changed.append(pid)
            result = env.file.save() if changed else before_raw
            check = UnityPy.load(result)
            try:
                require(state(check.file) == identity and check.file.objects.keys() == before.keys(), 'IDs, platform or external references changed')
                for pid, obj in check.file.objects.items():
                    require(sha(obj.get_raw_data()) == restored.get(pid, before[pid]), 'Unrelated object bytes changed: ' + name + ':' + str(pid))
            finally:
                check.file.reader.dispose()
            reports.append({'file': relative, 'sourceSha256': expected_source, 'beforeSha256': sha(before_raw), 'afterSha256': sha(result),
                            'audioObjects': count, 'changedObjects': changed, 'otherRawObjectsPreserved': len(before) - count, 'applied': commit})
            if changed:
                outputs.append((target, before_raw, result))
        finally:
            env.file.reader.dispose()
            source.file.reader.dispose()
    if commit:
        require(all(path.read_bytes() == before for path, before, result in outputs), 'Target changed concurrently')
        for path, before, result in outputs:
            temporary = path.with_name(path.name + '.restore-audio.tmp')
            require(not temporary.exists(), 'Inspect stale temporary before retry: ' + str(temporary))
            try:
                temporary.write_bytes(result)
                require(temporary.read_bytes() == result and path.read_bytes() == before, 'Write verification/concurrent-change failure')
                temporary.replace(path)
            finally:
                if temporary.exists():
                    temporary.unlink()
    return reports


def selftest(app):
    with tempfile.TemporaryDirectory(prefix='audio-restore-', dir=ROOT / 'work') as directory:
        fixture = Path(directory)
        for relative, _, _, _ in SOURCES.values():
            path = fixture / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes((app / relative).read_bytes())
        before = {p: sha(p.read_bytes()) for p in fixture.rglob('*') if p.is_file()}
        restore(fixture, False)
        require(all(sha(p.read_bytes()) == value for p, value in before.items()), 'Dry run modified fixture')
        first = restore(fixture, True)
        require(all(not row['changedObjects'] for row in restore(fixture, True)), 'Restore is not idempotent')
        path = fixture / SOURCES['globalgamemanagers'][0]
        env = UnityPy.load(path.read_bytes())
        obj = next(obj for obj in env.objects if obj.type.name == 'AudioManager')
        tree = obj.read_typetree()
        tree['m_Volume'] = 0.5
        obj.save_typetree(tree)
        path.write_bytes(env.file.save())
        env.file.reader.dispose()
        try:
            restore(fixture, True)
        except ValueError as error:
            require('preimage' in str(error), 'Unexpected guard failure: ' + str(error))
        else:
            raise AssertionError('Unknown preimage was accepted')
        return {'dryRunPreserved': True, 'idempotent': True, 'unknownPreimageRejected': True, 'first': first}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, default=ROOT / 'app')
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--output', type=Path, default=ROOT / 'work/evidence/mobile_revision/audio-restoration.json')
    args = parser.parse_args()
    report = selftest(args.app.resolve(strict=True)) if args.self_test else restore(args.app, args.apply)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('AUDIO_RESTORE_SELFTEST_PASS' if args.self_test else 'AUDIO_RESTORE_APPLIED' if args.apply else 'AUDIO_RESTORE_PLAN_PASS')
