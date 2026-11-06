"""Prepare a private Swappy-off comparison; never applies to the canonical app.

Unity 2020.3 maps PlayerSettings.Android.optimizedFramePacing to
GetAndroidUseSwappy/SetAndroidUseSwappy. This changes that serialized option
only, leaving native libraries, managed code, metadata and OBB untouched.
"""
import argparse
import hashlib
import json
from pathlib import Path

import UnityPy
from UnityPy.enums import BuildTarget
from prepare_depth_precision import container_layout

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'app/assets/bin/Data/globalgamemanagers'
RETAINED_SOURCE = ROOT / 'work/frame_pacing_input/globalgamemanagers'
WORK = ROOT / 'work/frame_pacing21'
BEFORE = '5797eb34a95da9cc8b27840c8177c9572b2063afa580b857dd279afb83d05217'
AFTER = '0f0b2fd9aeab7686c44a2b8433688bb213a4ef0ed72790f3591045c10ed97a21'
OFFSET = 1556
FIELD = 'androidUseSwappy'
MAPPING = ('https://github.com/Unity-Technologies/UnityCsReference/blob/2020.3/'
           'Editor/Mono/PlayerSettingsAndroid.bindings.cs#L485')


def require(ok, message):
    if not ok:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def verify(before, after):
    require(len(before) == 947200 and sha(before) == BEFORE, 'Exact source preimage required')
    require(len(after) == len(before), 'Container size changed')
    original = UnityPy.load(before)
    try:
        candidate = UnityPy.load(after)
        try:
            require(container_layout(candidate.file) == container_layout(original.file),
                    'Container metadata/layout changed')
            source_object = original.file.objects[1]
            target_object = candidate.file.objects[1]
            require(source_object.type.name == 'PlayerSettings' and source_object.byte_start == 1216
                    and source_object.byte_size == 723, 'PlayerSettings identity changed')
            old_tree = source_object.read_typetree()
            new_tree = target_object.read_typetree()
            require(old_tree[FIELD] is True and new_tree[FIELD] is False, 'Swappy flag transition differs')
            require(new_tree == {**old_tree, FIELD: False}, 'Unrelated PlayerSettings field changed')
            old_raw = source_object.get_raw_data()
            relative = OFFSET - source_object.byte_start
            require(old_raw[relative] == 1 and target_object.get_raw_data() ==
                    old_raw[:relative] + b'\0' + old_raw[relative + 1:], 'PlayerSettings raw delta differs')
            for pid, obj in original.file.objects.items():
                if pid != 1:
                    require(candidate.file.objects[pid].get_raw_data() == obj.get_raw_data(),
                            'Unrelated raw object changed: ' + str(pid))
            require(after[:OFFSET] + b'\1' + after[OFFSET + 1:] == before,
                    'Inverse delta did not restore the complete source')
            require(sha(after) == AFTER, 'Candidate hash differs')
            return {'preservedRawObjects': len(original.file.objects) - 1,
                    'playerSettingsRawBeforeSha256': sha(old_raw),
                    'playerSettingsRawAfterSha256': sha(target_object.get_raw_data()),
                    'unityVersion': original.file.unity_version,
                    'targetPlatform': int(original.file.target_platform),
                    'containerLayoutPreserved': True, 'inverseRestoresExactSource': True}
        finally:
            candidate.file.reader.dispose()
    finally:
        original.file.reader.dispose()


def prepare(raw):
    require(len(raw) == 947200 and sha(raw) == BEFORE, 'Exact source preimage required')
    require(raw[OFFSET] == 1, 'Expected enabled Swappy byte')
    candidate = raw[:OFFSET] + b'\0' + raw[OFFSET + 1:]
    return candidate, verify(raw, candidate)


def output_path(value):
    output = value.resolve()
    require(output == WORK or output.is_relative_to(WORK), 'Output must stay in work/frame_pacing21')
    require(not value.is_symlink() and not output.exists(), 'Output must be a fresh ordinary directory')
    return output


def self_test(raw, candidate):
    passed = ['exact_one_byte_candidate_and_full_inverse']

    def reject(name, action, message):
        try:
            action()
        except ValueError as error:
            require(message in str(error), 'Wrong rejection for ' + name + ': ' + str(error))
            passed.append(name)
            return
        raise AssertionError('Invalid candidate accepted: ' + name)

    reject('unknown_source', lambda: prepare(bytes([raw[0] ^ 1]) + raw[1:]), 'preimage')
    reject('truncated_source', lambda: prepare(raw[:-1]), 'preimage')
    reject('already_disabled_source', lambda: prepare(candidate), 'preimage')
    reject('unchanged_flag', lambda: verify(raw, raw), 'flag transition')
    reject('canonical_output', lambda: output_path(ROOT / 'app/frame-pacing-test'), 'work/frame_pacing21')

    def mutated(change):
        env = UnityPy.load(candidate)
        try:
            change(env.file)
            return env.file.save()
        finally:
            env.file.reader.dispose()

    bad = mutated(lambda file: setattr(file, '_m_target_platform', int(BuildTarget.StandaloneWindows64)))
    reject('changed_metadata', lambda: verify(raw, bad), 'metadata/layout')

    def change_settings(file):
        obj = file.objects[1]
        tree = obj.read_typetree()
        tree['androidBlitType'] = 1
        obj.save_typetree(tree)

    bad = mutated(change_settings)
    reject('unrelated_settings_field', lambda: verify(raw, bad), 'Unrelated PlayerSettings field')

    def change_object(file):
        obj = next(obj for pid, obj in file.objects.items() if pid != 1 and obj.byte_size)
        data = obj.get_raw_data()
        obj.set_raw_data(data[:-1] + bytes([data[-1] ^ 1]))

    bad = mutated(change_object)
    reject('unrelated_raw_object', lambda: verify(raw, bad), 'Unrelated raw object')
    # Byte1939 is alignment padding after PlayerSettings, outside every raw object.
    bad = candidate[:1939] + bytes([candidate[1939] ^ 1]) + candidate[1940:]
    reject('changed_container_padding', lambda: verify(raw, bad), 'Inverse delta')
    return passed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE,
                        help='Canonical file or pinned work/frame_pacing_input/globalgamemanagers preimage')
    parser.add_argument('--output', type=Path, default=WORK)
    parser.add_argument('--self-test', action='store_true', help='Validate positive/negative cases without writing')
    args = parser.parse_args()
    source = args.source.resolve()
    require(source in (SOURCE, RETAINED_SOURCE) and not args.source.is_symlink(),
            'Source must be the canonical file or the retained frame_pacing_input preimage')
    raw = source.read_bytes()
    candidate, receipt = prepare(raw)
    tests = self_test(raw, candidate)
    if args.self_test:
        print(json.dumps({'status': 'FRAME_PACING_GUARDS_PASS', 'tests': tests}, indent=2))
        return
    output = output_path(args.output)
    require(sha(source.read_bytes()) == BEFORE, 'Source changed concurrently')
    output.mkdir(parents=True)
    path = output / 'globalgamemanagers'
    with path.open('xb') as stream:
        stream.write(candidate)
    verify(raw, path.read_bytes())
    require(sha(source.read_bytes()) == BEFORE, 'Source changed concurrently')
    receipt.update(format='closing-shift-frame-pacing-comparison-v1', status='PREPARED_ONLY',
                   source=str(source), sourceSha256=BEFORE, candidate=str(path), candidateSha256=AFTER,
                   apkEntry='assets/bin/Data/globalgamemanagers', pathId=1, field=FIELD,
                   offset=OFFSET, before=1, after=0, changedBytes=1, tests=tests,
                   officialOption='PlayerSettings.Android.optimizedFramePacing', officialMapping=MAPPING,
                   bootConfigChanged=False, nativeOrManagedCodeChanged=False, metadataOrObbChanged=False,
                   canonicalOrDeviceModified=False, deviceAcceptance=False)
    with (output / 'manifest.json').open('x', encoding='utf-8') as stream:
        json.dump(receipt, stream, indent=2)
        stream.write('\n')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
