"""Verify the installed APK, its embedded assets or legacy OBB, and the stock metadata cache."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shlex
import struct
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from build_apk import (WHOLE_STORED_STARTUP_ASSETS, WHOLE_STORED_VIDEO_ASSETS, packing_policy, payload_compression,
                       payload_entries, split_data_file)

ADB = str(Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/platform-tools/adb.exe')
PACKAGE = 'com.chillasart.theclosingshift.port'


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def embedded_entries(archive, path, root, policy):
    name = path.relative_to(root).as_posix() + ('.resource' if path.name.endswith('.resS') else '')
    names = set(archive.namelist())
    parts = {value for value in names if re.fullmatch(re.escape(name) + r'\.split\d+', value)}
    if policy is not None:
        entries = payload_entries([path], root, compress=policy['compression'] == 'deflate')
    elif name in names:
        entries = [{'name': name, 'range_offset': 0, 'bytes': path.stat().st_size}]
    else:
        assert split_data_file(name), 'This native reader cannot open split basename: ' + name
        size = archive.getinfo(name + '.split0').file_size
        assert size > 0 and path.stat().st_size > 0, 'Invalid empty first segment'
        entries = [{'name': name + '.split' + str(index), 'range_offset': offset,
                    'bytes': min(size, path.stat().st_size-offset)}
                   for index, offset in enumerate(range(0, path.stat().st_size, size))]
    expected_parts = {row['name'] for row in entries if re.fullmatch(re.escape(name) + r'\.split\d+', row['name'])}
    assert parts == expected_parts, 'Missing or extra asset segments: ' + name
    assert (name in names) == (not expected_parts), 'Whole and split copies must not shadow each other: ' + name
    if path.name.endswith('.resS'):
        assert path.relative_to(root).as_posix() not in names, 'Unexpected unaliased resS payload'
    return entries


def verify_embedded_assets(archive, source, policy=None):
    """Check every virtual stream segment against the exact staged source range."""
    root = Path(source['path'])
    count, size, compressed = 0, 0, 0
    names = archive.namelist()
    assert len(names) == len(set(names)), 'Duplicate APK entries'
    assert not any(name.endswith('.obb') for name in names), 'Unexpected OBB inside the single APK'
    if policy is not None:
        assert policy == packing_policy(policy.get('compression') == 'deflate'), 'Unsupported packing policy'
    for name in names:
        split = re.fullmatch(r'(.*)\.split\d+', name)
        assert split is None or split_data_file(split[1]), 'Unsupported split bootstrap/Managed/native entry: ' + name
        assert split is None or split[1] not in WHOLE_STORED_VIDEO_ASSETS, 'Video sidecar must stay whole for direct FD access: ' + name
        if name in WHOLE_STORED_VIDEO_ASSETS:
            assert archive.getinfo(name).compress_type == zipfile.ZIP_STORED, 'Video sidecar must be ZIP_STORED for direct FD access: ' + name
        assert split is None or split[1] not in WHOLE_STORED_STARTUP_ASSETS, 'Startup asset must stay whole: ' + name
        if name in WHOLE_STORED_STARTUP_ASSETS:
            assert archive.getinfo(name).compress_type == zipfile.ZIP_STORED, 'Startup asset must be ZIP_STORED: ' + name
    for row in source['bulk']:
        path = Path(row['stagedPath'])
        assert path.resolve().is_relative_to(root) and sha(path) == row['source']['sha256'], 'Staged bulk changed'
        digest = hashlib.sha256()
        entries = embedded_entries(archive, path, root, policy)
        for entry in entries:
            info = archive.getinfo(entry['name'])
            assert info.file_size == entry['bytes'] and info.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) and info.extract_version < 45, 'Embedded asset size/compression differs: ' + entry['name']
            if policy is not None:
                assert info.compress_type == payload_compression(entry['name'], policy['compression'] == 'deflate'), 'Declared payload compression differs'
            with path.open('rb') as original, archive.open(info) as stream:
                original.seek(entry['range_offset'])
                while data := stream.read(1024 * 1024):
                    assert data == original.read(len(data)), 'Embedded range differs: ' + entry['name']
                    digest.update(data)
            count += 1
            size += info.file_size
            compressed += info.compress_size
        assert digest.hexdigest() == row['source']['sha256'], 'Embedded concatenated source differs: ' + str(path)
    return {'entries': count, 'bytes': size, 'compressedBytes': compressed, 'allSourceRangesVerified': True,
            'wholeStoredVideoAssets': sorted(WHOLE_STORED_VIDEO_ASSETS.intersection(names)),
            'wholeStoredStartupAssets': [name for name in WHOLE_STORED_STARTUP_ASSETS if name in names],
            'packingPolicy': policy if policy is not None else 'inferred from actual part0 sizes'}


def recorded_policy(apk, apk_sha):
    metadata = apk.with_suffix('.build.json')
    if not metadata.is_file():
        return None
    document = json.loads(metadata.read_text(encoding='utf-8'))
    artifacts = [row for row in document['artifacts'] if row['sha256'] == apk_sha]
    assert len(artifacts) == 1, 'Packing receipt does not identify this APK'
    policy = artifacts[0].get('packingPolicy', document.get('packingPolicy'))
    if 'packingPolicy' in document:
        assert policy == document['packingPolicy'], 'Artifact and package packing policies differ'
    return policy


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-dir', required=True, type=Path)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--apk', type=Path, help='Explicit manifest-only variant of this build')
    parser.add_argument('--obb', type=Path, help='Explicit asset-only OBB variant of this build')
    parser.add_argument('--receipt', type=Path, help='New work-only receipt for an explicit APK/OBB variant')
    args = parser.parse_args()
    build = args.build_dir.resolve(strict=True)
    assert build.is_relative_to(ROOT / 'work'), 'Expected a build inside this project work'

    def adb(*command, binary=False):
        result = subprocess.run([ADB, '-s', args.serial, *command], capture_output=True, timeout=180)
        result.check_returncode()
        return result.stdout if binary else result.stdout.decode('utf-8', 'replace').strip()

    def remote_sha(path):
        result = adb('shell', 'sha256sum ' + shlex.quote(path)).split()[0]
        assert len(result) == 64, path
        return result

    compiled = json.loads((build / 'compile-result.json').read_text(encoding='utf-8'))
    converted = json.loads((build / 'convert-result.json').read_text(encoding='utf-8'))
    apk = args.apk.resolve(strict=True) if args.apk else build / 'package/base.apk'
    assert apk.is_relative_to(ROOT / 'work'), 'Expected an APK inside this project work'
    apk_sha = sha(apk)
    policy = recorded_policy(apk, apk_sha)
    staged = json.loads((build / 'stage-app-result.json').read_text(encoding='utf-8'))
    package_info = adb('shell', 'dumpsys package ' + PACKAGE)
    installed_version = int(re.search(r'\bversionCode=(\d+)', package_info)[1])
    embedded = staged.get('dataContainer') == 'apk'
    source = staged['assetSource'] if embedded else staged['obbSource']
    if not args.apk:
        assert installed_version == source['versionCode'], 'Installed version differs from the staged build'
    # An explicit Activity-only APK may bump versionCode while retaining the native build.
    obb_name = 'main.' + str(installed_version) + '.' + PACKAGE + '.obb'
    obb = None if embedded else args.obb.resolve(strict=True) if args.obb else build / 'package/obb' / obb_name
    if embedded:
        assert not args.obb, 'A single-APK build does not accept an OBB override'
        for kind in ('main', 'patch'):
            matching_obb = '/sdcard/Android/obb/' + PACKAGE + '/' + obb_name.replace('main.', kind + '.', 1)
            assert adb('shell', 'if [ -e ' + shlex.quote(matching_obb) + ' ]; then echo present; fi') != 'present', 'Matching external OBB can shadow APK assets'
    else:
        assert obb.is_relative_to(ROOT / 'work') and obb.name == obb_name, 'Expected this build version OBB inside work'
    asset_verification = None
    native_compressed = False
    with zipfile.ZipFile(apk) as archive:
        for name in ('libmain.so', 'libunity.so', 'libil2cpp.so'):
            with archive.open('lib/arm64-v8a/' + name) as stream:
                assert hashlib.file_digest(stream, 'sha256').hexdigest() == sha(build / 'app/lib/arm64-v8a' / name), 'APK native differs: ' + name
        with archive.open('assets/bin/Data/Managed/Metadata/global-metadata.dat') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest() == compiled['metadataSha256'], 'APK metadata differs'
        if embedded:
            asset_verification = verify_embedded_assets(archive, source, policy)
            native_methods = {info.compress_type for info in archive.infolist() if info.filename.startswith('lib/') and info.filename.endswith('.so')}
            assert native_methods <= {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}, 'Unsupported native compression'
            if policy is not None:
                assert native_methods == {zipfile.ZIP_DEFLATED if policy['compression'] == 'deflate' else zipfile.ZIP_STORED}, 'Native compression differs from the packing policy'
            native_compressed = native_methods == {zipfile.ZIP_DEFLATED}
    paths = adb('shell', 'pm path ' + PACKAGE).splitlines()
    assert len(paths) == 1 and paths[0].startswith('package:'), 'Expected one installed base APK'
    installed = paths[0][8:]
    obb_sha = sha(obb) if obb else None
    assert remote_sha(installed) == apk_sha, 'Installed APK differs'
    if obb:
        assert remote_sha('/sdcard/Android/obb/' + PACKAGE + '/' + obb.name) == obb_sha, 'Installed OBB differs'
    files = '/sdcard/Android/data/' + PACKAGE + '/files/il2cpp'
    metadata = remote_sha(files + '/Metadata/global-metadata.dat')
    assert metadata == compiled['metadataSha256'] == converted['metadata']['sha256'], 'Metadata cache differs'
    raw_version = adb('exec-out', 'cat', files + '/unity.ver', binary=True)
    assert len(raw_version) == 8, 'Expected uint64-le unity.ver'
    version = struct.unpack('<Q', raw_version)[0]
    assert version == int(converted['stockMetadataCache']['utcTicks']), 'Metadata cache version differs'
    assert sha(build / 'libil2cpp.so') == compiled['library']['sha256'], 'Host native library changed'
    compat = re.search(r'(?m)^\s+pageSizeCompat=(\d+)\s*$', package_info)
    receipt = dict(model=adb('shell', 'getprop ro.product.model'), android=adb('shell', 'getprop ro.build.version.release'), apkSHA256=apk_sha, obbSHA256=obb_sha,
                   nativeSHA256=compiled['library']['sha256'], metadataSHA256=metadata,
                   unityVer=version, kernelPageSize=int(adb('shell', 'getconf PAGESIZE')),
                   pageSizeCompatFlag=int(compat[1]) if compat else None,
                   versionCode=installed_version, targetSdk=int(re.search(r'\btargetSdk=(\d+)', package_info)[1]),
                   stockCacheUpdatedWithoutDeletion=True)
    if embedded:
        receipt.update(gameDataContainer='apk', embeddedAssets=asset_verification,
                       externalObbRequired=False, nativeLibrariesCompressed=native_compressed)
    receipt_path = args.receipt.resolve() if args.receipt else build / 'installed-device-verified.json'
    if args.receipt:
        assert receipt_path.is_relative_to(ROOT / 'work') and not receipt_path.exists(), 'Choose a new work-only variant receipt'
    receipt_path.write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
