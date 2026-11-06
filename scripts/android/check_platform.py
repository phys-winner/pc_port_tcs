"""Compile the current Android sources and check display, log rotation and ELF guards."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    manifest = ET.parse(ROOT / 'app/AndroidManifest.xml').getroot()
    android_ns = '{http://schemas.android.com/apk/res/android}'
    permissions = {node.get(android_ns + 'name') for node in manifest.findall('uses-permission')}
    assert not permissions.intersection({'android.permission.MANAGE_EXTERNAL_STORAGE', 'android.permission.READ_EXTERNAL_STORAGE', 'android.permission.WRITE_EXTERNAL_STORAGE'})
    assert android_ns + 'requestLegacyExternalStorage' not in manifest.find('application').attrib
    activity = (ROOT / 'app/src/com/unity3d/player/UnityPlayerActivity.java').read_text(encoding='utf-8')
    assert 'requestPermissions(' not in activity and 'ACTION_MANAGE_' not in activity
    storage_source = (ROOT / 'app/src/com/chillasart/theclosingshift/port/PortStorage.java').read_text(encoding='utf-8')
    assert 'context.getExternalFilesDir(null)' in storage_source and 'getExternalStoragePublicDirectory' not in storage_source
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'work') or output.exists():
        raise ValueError('Choose a new check directory inside this project work')
    output.mkdir(parents=True)
    config = json.loads((Path(os.environ['USERPROFILE']) / '.agents/skills/adapt-arm32-game/machine_config.json').read_text())
    java = Path(config['java_exe'])
    android = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/platforms/android-36/android.jar'
    unity = ROOT / 'work/tools/Unity2020/Editor/Data/PlaybackEngines/AndroidPlayer/Variations/il2cpp/Release/Classes/classes.jar'
    classes = output / 'classes'
    classes.mkdir()
    sources = sorted((ROOT / 'app/src').rglob('*.java')) + [Path(__file__).with_name(name) for name in ('PortPlatformFixture.java', 'PortStartupPolicyFixture.java', 'PortStorageFixture.java')]

    def run(arguments):
        result = subprocess.run(list(map(str, arguments)), capture_output=True, text=True, encoding='utf-8', errors='replace')
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        return result.stdout.strip()

    run([java.with_name('javac.exe'), '--release', '8', '-classpath', os.pathsep.join(map(str, [android, unity])), '-d', classes, *sources])
    platform = run([java, '-classpath', os.pathsep.join(map(str, [classes, android])), 'com.chillasart.theclosingshift.port.PortPlatformFixture', output / 'log-fixture'])
    startup = run([java, '-classpath', os.pathsep.join(map(str, [classes, android])), 'PortStartupPolicyFixture'])
    storage = run([java, '-classpath', os.pathsep.join(map(str, [classes, android])), 'com.chillasart.theclosingshift.port.PortStorageFixture', output / 'storage-fixture'])
    spec = importlib.util.spec_from_file_location('arm64_build', ROOT / 'scripts/arm64_il2cpp/actual_build.py')
    pipeline = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(pipeline)
    with zipfile.ZipFile(ROOT / 'pub/base.apk') as archive:
        raw = archive.read('lib/arm64-v8a/libil2cpp.so')
    original = output / 'original.so'
    original.write_bytes(raw)
    elf = pipeline.elf_info(original)
    phoff = struct.unpack_from('<Q', raw, 32)[0]
    size, count = struct.unpack_from('<HH', raw, 54)
    for name, mutate in [('tls', lambda b: struct.pack_into('<I', b, phoff, 7)),
                         ('alignment', lambda b: struct.pack_into('<Q', b, next(phoff + i * size for i in range(count) if struct.unpack_from('<I', b, phoff + i * size)[0] == 1) + 48, 4096))]:
        changed = bytearray(raw)
        mutate(changed)
        candidate = output / (name + '.so')
        candidate.write_bytes(changed)
        try:
            pipeline.elf_info(candidate)
        except ValueError as error:
            assert ('ELF TLS' if name == 'tls' else 'LOAD alignment') in str(error), str(error)
        else:
            raise AssertionError('Unsafe ELF accepted: ' + name)
    report = dict(java=platform, startup=startup, storage=storage, elf=elf, elfGuardCases=3,
                  sourceSHA256={p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources})
    (output / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(platform)
    print(startup)
    print(storage)
    print('ELF_GUARDS_PASS cases=3')


if __name__ == '__main__':
    main()
