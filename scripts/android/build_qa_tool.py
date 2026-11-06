"""Build the same-signature live FPS instrumentation; never installs or runs it."""
import argparse
import importlib.util
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'work') or output.exists():
        raise ValueError('Choose a new output directory inside this project work')
    output.mkdir(parents=True)
    packager = module('apk_packager', ROOT / 'scripts/build_apk.py')
    pipeline = module('arm64_build', ROOT / 'scripts/arm64_il2cpp/actual_build.py')
    config = json.loads(packager.MACHINE_CONFIG.read_text())
    tools = Path(config['project_root']) / 'tools/android/sdk/build-tools' / config['build_tools_version']
    java = Path(config['java_exe'])
    android = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/platforms/android-36/android.jar'
    llvm = pipeline.NDK / 'toolchains/llvm/prebuilt/windows-x86_64'
    app = output / 'app'
    native = app / 'lib/arm64-v8a'
    native.mkdir(parents=True)
    classes = output / 'classes'
    classes.mkdir()
    dex = output / 'dex'
    dex.mkdir()
    (app / 'AndroidManifest.xml').write_text('<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="com.chillasart.theclosingshift.audit" android:versionCode="4" android:versionName="4"><uses-sdk android:minSdkVersion="23" android:targetSdkVersion="32"/><application android:hasCode="true" android:extractNativeLibs="true" android:label="The Closing Shift QA"/><instrumentation android:name="com.chillasart.tools.PortAudit" android:targetPackage="com.chillasart.theclosingshift.port"/><instrumentation android:name="com.chillasart.tools.StorageProbe" android:targetPackage="com.chillasart.theclosingshift.port"/></manifest>')
    packager.run([llvm / 'bin/clang++.exe', '--target=aarch64-linux-android23', '--sysroot=' + str(llvm / 'sysroot'), '-shared', '-fPIC', '-std=c++11', '-O2', '-Wl,-z,max-page-size=16384', '-I' + str(pipeline.DATA / 'il2cpp/libil2cpp'), ROOT / 'scripts/android/port_audit.cpp', '-ldl', '-o', native / 'libportaudit.so'], output / 'native.log')
    elf = pipeline.elf_info(native / 'libportaudit.so')
    assert elf['machine'] == 183 and elf['native16kbLayout']
    packager.run([java.with_name('javac.exe'), '--release', '8', '-classpath', android, '-d', classes, ROOT / 'scripts/android/PortAudit.java', ROOT / 'scripts/android/StorageProbe.java'], output / 'javac.log')
    readiness = packager.run([java, '-classpath', os.pathsep.join(map(str, [classes, android])), 'com.chillasart.tools.PortAudit', '--self-test', output / 'readiness-fixture'], output / 'readiness.log')
    assert readiness.strip() == 'QA_READINESS_PASS cases=4'
    backup = packager.run([java, '-classpath', os.pathsep.join(map(str, [classes, android])), 'com.chillasart.tools.StorageProbe', '--self-test', output / 'backup-fixture'], output / 'backup-fixture.log')
    assert backup.strip() == 'STORAGE_BACKUP_PASS assertions=8'
    packager.run([java, '-cp', tools / 'lib/d8.jar', 'com.android.tools.r8.D8', '--min-api', '23', '--lib', android, '--output', dex, *classes.rglob('*.class')], output / 'd8.log')
    signer_dir = Path(config['signer']['dir'])
    signer = {key: signer_dir / config['signer'][field] for key, field in [('jar', 'apksigner'), ('key', 'key'), ('cert', 'cert')]}
    signer['expected'] = config['signer']['expected_cert_sha256'].lower()
    receipt = packager.package_target(app, output / 'audit.apk', list(dex.glob('classes*.dex')), tools, java, signer, android)
    receipt['path'] = 'audit.apk'
    receipt['elf'] = elf
    (output / 'build.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
