"""Reproduce the hardware-accepted API28+ cutout policy; guarded atomic apply is explicit."""
import argparse
import hashlib
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'app/src/com/unity3d/player/UnityPlayerActivity.java'
OLD_SHA = '150a4c4414f8e6b843e60480768b09526b79924b7133639d0849aa09eaee7c94'
NEW_SHA = 'd2f018abb52bf50d01a529d7719d8f852cfb15115f6174c8040ebdf4896bb0fb'
STAGED_ACCEPTED_SHA = '807ad3c1b076a3ea91cb99c7e3ccb9f8b05fbc20b65f935940f291c27bc0f946'
CALLBACK = '''    // Preserve the app's cutout policy when Unity updates other window attributes.
    @Override public void onWindowAttributesChanged(WindowManager.LayoutParams attributes)
    {
        if (android.os.Build.VERSION.SDK_INT >= 28)
            attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
        super.onWindowAttributesChanged(attributes);
    }

'''
INITIAL = '''        if (android.os.Build.VERSION.SDK_INT >= 28) {
            WindowManager.LayoutParams attributes = getWindow().getAttributes();
            attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            getWindow().setAttributes(attributes);
        }
'''
CHANGES = [('    // Setup activity layout\n', CALLBACK + '    // Setup activity layout\n'),
           ('        setContentView(mUnityPlayer);\n', '        setContentView(mUnityPlayer);\n' + INITIAL)]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def transform(raw):
    identity = sha(raw)
    if identity not in (OLD_SHA, NEW_SHA):
        raise ValueError('Unreviewed canonical Activity preimage: ' + identity)
    text = raw.decode('utf-8'); newline = '\r\n' if '\r\n' in text else '\n'
    text = text.replace('\r\n', '\n')
    if identity == NEW_SHA:
        reverse = text
        for before, after in reversed(CHANGES):
            if reverse.count(after) != 1: raise ValueError('Accepted callback structure changed')
            reverse = reverse.replace(after, before, 1)
        assert sha(reverse.replace('\n', newline).encode()) == OLD_SHA
        return raw, False
    result = text
    for before, after in CHANGES:
        if result.count(before) != 1: raise ValueError('Activity insertion anchor changed')
        result = result.replace(before, after, 1)
    reverse = result
    for before, after in reversed(CHANGES): reverse = reverse.replace(after, before, 1)
    assert reverse == text
    # The canonical32-bit startup path stays intact; native ARM64 staging removes it separately.
    for preserved in ('LargeObbIO.install(', 'Large OBB IO initialization failed:', 'ControllerBridge.install(this);', 'PortConfig.seed(this);'):
        assert text.count(preserved) == result.count(preserved) == 1
    output = result.replace('\n', newline).encode()
    assert sha(output) == NEW_SHA
    return output, True


def checked_target(path):
    if path.is_symlink(): raise ValueError('Symlink Activity target is not allowed')
    target = path.resolve(strict=True)
    if target.name != 'UnityPlayerActivity.java' or not target.is_file(): raise ValueError('Expected Activity source file')
    if target != SOURCE.resolve(strict=True) and not target.is_relative_to(ROOT / 'work'):
        raise ValueError('Target must be canonical Activity or a contained work copy')
    return target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if args.apply and args.output: parser.error('--apply and --output are mutually exclusive')
    target = checked_target(args.target); raw = target.read_bytes(); result, changed = transform(raw)
    writes = 0
    if args.output:
        output = args.output.resolve()
        if not output.is_relative_to(ROOT / 'work') or output.exists() or output.name != SOURCE.name:
            raise ValueError('Candidate output must be a new Activity file inside work')
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open('xb') as stream: stream.write(result)
        writes = 1
    elif args.apply and changed:
        temporary = target.with_name(target.name + '.cutout.tmp')
        if temporary.exists() or temporary.is_symlink(): raise ValueError('Temporary path already exists')
        with temporary.open('xb') as stream: stream.write(result)
        assert temporary.read_bytes() == result and target.read_bytes() == raw
        os.replace(temporary, target)
        assert target.read_bytes() == result
        writes = 1
    else:
        assert target.read_bytes() == raw
    print(json.dumps(dict(status='CUTOUT_ACTIVITY_APPLIED' if args.apply else 'CUTOUT_ACTIVITY_PREFLIGHT_PASS',
        target=str(target), state='unpatched' if changed else 'accepted', beforeSHA256=sha(raw),
        acceptedSHA256=NEW_SHA, writes=writes, insertedLines=13, unrelatedSourceBytesPreserved=True,
        canonicalLargeObbIOPreserved=True), indent=2))


if __name__ == '__main__':
    if not __debug__: raise RuntimeError('Strict Activity guards require Python without -O')
    main()
