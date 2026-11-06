"""Compile/D8 and execute exact accepted callback tests; writes only work diagnostics."""
import argparse
import json
import os
import subprocess
from pathlib import Path

from prepare_cutout_activity import ROOT, SOURCE, OLD_SHA, NEW_SHA, STAGED_ACCEPTED_SHA, CALLBACK, INITIAL, CHANGES, sha, transform


def run(arguments, log):
    result = subprocess.run(list(map(str, arguments)), capture_output=True, text=True)
    log.write_text(result.stdout + result.stderr)
    if result.returncode: raise RuntimeError(result.stdout + result.stderr)
    return result.stdout.strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, default=ROOT / 'app/src')
    parser.add_argument('--activity', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=ROOT / 'work/cutout_java_check')
    args = parser.parse_args(); source_root = args.source_root.resolve(strict=True); activity = args.activity.resolve(strict=True)
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'work') or output.exists(): raise ValueError('Use a new work-only check directory')
    if not source_root.is_relative_to(ROOT) or not activity.is_relative_to(ROOT): raise ValueError('Sources must belong to this workspace')
    raw = activity.read_bytes(); text = raw.decode().replace('\r\n', '\n')
    if sha(raw) not in (NEW_SHA, STAGED_ACCEPTED_SHA): raise ValueError('Unreviewed accepted Activity source')
    assert text.count(CALLBACK) == 1 and text.count(INITIAL) == 1
    assert 'setAttributes' not in CALLBACK and CALLBACK.count('super.onWindowAttributesChanged(attributes);') == 1
    canonical = SOURCE.read_bytes()
    if sha(canonical) == NEW_SHA:
        old = canonical.decode('utf-8'); newline = '\r\n' if '\r\n' in old else '\n'
        old = old.replace('\r\n', '\n')
        for before, after in reversed(CHANGES): old = old.replace(after, before, 1)
        old = old.replace('\n', newline).encode('utf-8')
    else:
        old = canonical
    assert sha(old) == OLD_SHA
    accepted, changed = transform(old)
    assert sha(accepted) == NEW_SHA and changed
    assert transform(accepted) == (accepted, False)
    try:
        transform(old + b'\n')
    except ValueError:
        pass
    else:
        raise AssertionError('Unreviewed Activity preimage was accepted')
    sources = sorted(source_root.rglob('*.java'))
    originals = [p for p in sources if p.relative_to(source_root).as_posix() == 'com/unity3d/player/UnityPlayerActivity.java']
    assert len(originals) == 1
    sources = [activity if p == originals[0] else p for p in sources]
    assert len(sources) in (3, 4) and not any(p.is_symlink() for p in sources)
    inputs = {str(p): sha(p.read_bytes()) for p in sources}
    config = json.loads((Path.home() / '.agents/skills/adapt-arm32-game/machine_config.json').read_text())
    java = Path(config['java_exe']); javac = java.with_name('javac.exe')
    android = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/platforms/android-32/android.jar'
    unity = ROOT / 'work/tools/Unity2020/Editor/Data/PlaybackEngines/AndroidPlayer/Variations/il2cpp/Release/Classes/classes.jar'
    d8 = Path(config['project_root']) / 'tools/android/sdk/build-tools' / config['build_tools_version'] / 'lib/d8.jar'
    for path in (java, javac, android, unity, d8):
        if not path.is_file(): raise FileNotFoundError(path)
    assert sha(unity.read_bytes()) == '0990586f519f2c6937569c8ac1120f29977b4c0d6855ae886b4d464b25476890'
    output.mkdir(parents=True); classes = output / 'classes'; dex = output / 'dex'; classes.mkdir(); dex.mkdir()
    run([javac, '--release', '8', '-classpath', os.pathsep.join(map(str, (android, unity))), '-d', classes, *sources], output / 'javac.log')
    run([java, '-cp', d8, 'com.android.tools.r8.D8', '--min-api', '23', '--lib', android, '--output', dex, unity,
        *sorted(classes.rglob('*.class'))], output / 'd8.log')
    assert list(dex.glob('classes*.dex'))
    fixture = output / 'callback-fixture'; (fixture / 'android/os').mkdir(parents=True)
    (fixture / 'android/os/Build.java').write_text('package android.os; public final class Build { public static final class VERSION { public static int SDK_INT; } }')
    (fixture / 'WindowManager.java').write_text('final class WindowManager { static class LayoutParams { static final int LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES=1; int layoutInDisplayCutoutMode, flags; } }')
    (fixture / 'Activity.java').write_text('class Activity { int callbacks; WindowManager.LayoutParams received; public void onWindowAttributesChanged(WindowManager.LayoutParams p) { callbacks++; received=p; } }')
    # The callback is copied from the source under test, not reimplemented in the fixture.
    callback = text[text.index(CALLBACK):text.index(CALLBACK) + len(CALLBACK)]
    harness = '''public final class CallbackCheck extends Activity {
''' + callback + '''    public static void main(String[] args) {
        CallbackCheck owner=new CallbackCheck(); WindowManager.LayoutParams p=new WindowManager.LayoutParams(); p.flags=0x1706;
        android.os.Build.VERSION.SDK_INT=27; p.layoutInDisplayCutoutMode=2; owner.onWindowAttributesChanged(p);
        if(p.layoutInDisplayCutoutMode!=2||p.flags!=0x1706||owner.callbacks!=1||owner.received!=p)throw new AssertionError("API27 passthrough");
        android.os.Build.VERSION.SDK_INT=28; p.layoutInDisplayCutoutMode=0; owner.onWindowAttributesChanged(p);
        if(p.layoutInDisplayCutoutMode!=1||p.flags!=0x1706||owner.callbacks!=2)throw new AssertionError("API28 request");
        android.os.Build.VERSION.SDK_INT=30; p.layoutInDisplayCutoutMode=2; owner.onWindowAttributesChanged(p);
        if(p.layoutInDisplayCutoutMode!=1||p.flags!=0x1706||owner.callbacks!=3)throw new AssertionError("Runtime overwrite repair");
        owner.onWindowAttributesChanged(p);
        if(p.layoutInDisplayCutoutMode!=1||owner.callbacks!=4)throw new AssertionError("Idempotence and no recursion");
        System.out.println("CUTOUT_CALLBACK_PASS 4 exact-source cases");
    }
}
'''
    (fixture / 'CallbackCheck.java').write_text(harness); host = fixture / 'classes'; host.mkdir()
    run([javac, '--release', '8', '-d', host, *sorted(fixture.rglob('*.java'))], output / 'callback-javac.log')
    callback_result = run([java, '-classpath', host, 'CallbackCheck'], output / 'callback-check.log')
    assert inputs == {str(p): sha(p.read_bytes()) for p in sources}
    report = dict(status='CUTOUT_JAVA_D8_PASS', activitySHA256=sha(raw), inputs=inputs, javacAndroid32=True,
        d8MinimumAPI23=True, guardCases=3, callbackCases=4, callbackResult=callback_result, sourceOrDeviceWrites=False,
        workOutput=str(output), hardwareAccepted='revision13: shortEdges and frame[0,0][2340,1080]')
    (output / 'manifest.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    if not __debug__: raise RuntimeError('Strict Java checks require Python without -O')
    main()
