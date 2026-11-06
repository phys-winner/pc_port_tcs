"""ARM64-only IL2CPP stages. Plan is read-only; prepare requires frozen controls."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
import shutil
import struct
import subprocess
import time
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
EDITOR = ROOT / 'work/tools/Unity2020/Editor'
DATA = EDITOR / 'Data'
ANDROID = DATA / 'PlaybackEngines/AndroidPlayer'
SOURCE = ROOT / 'app/assets/bin/Data/Managed'
AOT = ANDROID / 'Variations/il2cpp/Managed'
BCL = DATA / 'MonoBleedingEdge/lib/mono/unityaot'
IL2CPP = DATA / 'il2cpp/build/deploy/netcoreapp3.1/il2cpp.exe'
NDK = ANDROID / 'NDK'
BASELIB = ANDROID / 'Variations/il2cpp/Release/StaticLibs/arm64-v8a'
NATIVE = ANDROID / 'Variations/il2cpp/Release/Libs/arm64-v8a'
INSPECTOR = Path(__file__).parent / 'inspect_managed/bin/Release/net10.0/inspect_managed.dll'
AOT_PATCHER = Path(__file__).parent / 'patch_aot/bin/Release/net10.0/patch_aot.dll'
AOT_HELPER = ROOT / 'work/aot_compat/ClosingShift.AotCompat.dll'
EXCLUDED = {'ClosingShift.AuraProbe.dll'}
REQUIRED = {'Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll', 'Unity.Postprocessing.Runtime.dll',
            'ClosingShift.Android.dll', 'ClosingShift.RussianLocale.dll', 'ClosingShift.RussianFonts.dll',
            'ClosingShift.MobileRender.dll', 'ClosingShift.AuraGles.dll'}
DOTNET_UNIX_EPOCH_TICKS = 621355968000000000
DOTNET_MAX_TICKS = 3155378975999999999

def require(condition, message):
    if not condition: raise ValueError(message)

def sha(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()

def describe(path):
    require(path.is_file(), 'Missing local input: ' + str(path))
    return {'path': str(path.resolve()), 'bytes': path.stat().st_size, 'sha256': sha(path)}

def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')

def new_cache_stamp(previous=(), now_ns=None):
    # Exact AndroidBuildPostprocessor uses DateTime.UtcNow.Ticks. Freeze once,
    # with a monotonic collision guard for a coarse/backwards host clock.
    ticks = DOTNET_UNIX_EPOCH_TICKS + (time.time_ns() if now_ns is None else now_ns) // 100
    ticks = max(ticks, max(previous, default=0) + 1)
    require(0 < ticks <= DOTNET_MAX_TICKS, 'Stock buildDate is outside positive .NET UTC ticks')
    return {'bootConfigKey': 'buildDate', 'utcTicks': str(ticks), 'unityVerEncoding': 'uint64-le'}

def validate_cache_stamp(stamp):
    require(isinstance(stamp, dict) and set(stamp) == {'bootConfigKey', 'utcTicks', 'unityVerEncoding'},
            'Fresh prepare with a frozen stock metadata-cache stamp required; historical inputs are immutable')
    value = stamp['utcTicks']
    require(stamp['bootConfigKey'] == 'buildDate' and stamp['unityVerEncoding'] == 'uint64-le'
            and isinstance(value, str) and value.isascii() and value.isdecimal()
            and str(int(value)) == value and 0 < int(value) <= DOTNET_MAX_TICKS,
            'Invalid exact Unity buildDate cache identity')
    return value

def known_cache_dates():
    dates = []
    for path in (ROOT / 'work').rglob('inputs.json'):
        try:
            document = json.loads(path.read_text(encoding='utf-8'))
        except (UnicodeError, json.JSONDecodeError):
            continue
        if isinstance(document, dict) and document.get('format') == 'closing-shift-arm64-il2cpp-v1' and 'stockMetadataCache' in document:
            dates.append(int(validate_cache_stamp(document['stockMetadataCache'])))
    return dates

def stamped_boot_config(raw, stamp):
    value = validate_cache_stamp(stamp).encode('ascii')
    require(b'\0' not in raw, 'Unexpected NUL in boot.config')
    lines = raw.splitlines(keepends=True)
    matches = [index for index, line in enumerate(lines) if line.split(b'=', 1)[0] == b'buildDate']
    require(len(matches) <= 1, 'Duplicate stock buildDate in source boot.config')
    if matches:
        index = matches[0]
        old = lines[index]
        ending = b'\r\n' if old.endswith(b'\r\n') else b'\n' if old.endswith(b'\n') else b'\r' if old.endswith(b'\r') else b''
        lines[index] = b'buildDate=' + value + ending
        result = b''.join(lines)
        require(all(line == raw.splitlines(keepends=True)[i] for i, line in enumerate(lines) if i != index),
                'Unrelated boot.config line changed')
    else:
        result = raw + (b'' if not raw or raw.endswith((b'\n', b'\r')) else b'\n') + b'buildDate=' + value + b'\n'
        require(result.startswith(raw), 'Unrelated boot.config prefix changed')
    require([line for line in result.splitlines() if line.startswith(b'buildDate=')] == [b'buildDate=' + value],
            'Persisted buildDate differs from frozen stamp')
    return result

def cache_identity(manifest, converted, compiled=None):
    stamp = manifest.get('stockMetadataCache')
    validate_cache_stamp(stamp)
    expected = {**stamp, 'metadataSha256': converted['metadata']['sha256']}
    require(converted.get('stockMetadataCache') == expected, 'Converted metadata/cache identity differs from frozen inputs')
    if compiled is not None:
        require(compiled.get('metadataSha256') == expected['metadataSha256'], 'Compiled native/metadata identity differs')
    return expected

def stage_boot_config(build, destination, identity):
    path = destination / 'assets/bin/Data/boot.config'
    require(path.resolve().is_relative_to(build / 'app'), 'Stock boot stamp must stay in this work app')
    before = describe(path)
    raw = path.read_bytes()
    stamp = {key: identity[key] for key in ('bootConfigKey', 'utcTicks', 'unityVerEncoding')}
    expected = stamped_boot_config(raw, stamp)
    path.write_bytes(expected)
    require(path.read_bytes() == expected, 'Staged stock boot stamp changed on write')
    return {**identity, 'before': before, 'after': describe(path), 'canonicalBootConfigUnchanged': True}

def generated_inputs(build):
    return sorted(path for path in (build / 'cpp').rglob('*')
                  if path.is_file() and path.suffix in ('.c', '.cpp', '.h')
                  and 'lumpedcpp' not in path.relative_to(build / 'cpp').parts)

def run(args, log):
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open('w', encoding='utf-8') as stream:
        result = subprocess.run(list(map(str, args)), cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
    require(result.returncode == 0, 'Stage failed; inspect ' + str(log))

def elf_info(path):
    raw = path.read_bytes()
    require(raw[:6] == b'\x7fELF\x02\x01', 'Expected little-endian ELF64: ' + str(path))
    machine = struct.unpack_from('<H', raw, 18)[0]
    require(machine == 183, 'Expected AArch64 ELF, never ARM32: ' + str(path))
    phoff = struct.unpack_from('<Q', raw, 32)[0]
    size, count = struct.unpack_from('<HH', raw, 54)
    require(size == 56, 'Unexpected ELF program header size')
    segments = [struct.unpack_from('<IIQQQQQQ', raw, phoff + i * size) for i in range(count)]
    require(not any(s[0] == 7 for s in segments), 'ELF TLS is incompatible with this port minSdk 23')
    require(all(s[7] >= 16384 for s in segments if s[0] == 1), 'ELF LOAD alignment must support 16-KB pages')
    def offset(address):
        return next(s[2] + address - s[3] for s in segments if s[0] == 1 and s[3] <= address < s[3] + s[5])
    dynamic = next((s for s in segments if s[0] == 2), None)
    entries = [] if dynamic is None else [struct.unpack_from('<qQ', raw, p) for p in range(dynamic[2], dynamic[2] + dynamic[5], 16)]
    names = []
    string_address = next((value for tag, value in entries if tag == 5), None)
    if string_address is not None:
        base = offset(string_address)
        for tag, value in entries:
            if tag == 1:
                start = base + value
                names.append(raw[start:raw.index(b'\0', start)].decode('ascii'))
    loads = [s for s in segments if s[0] == 1]
    minimum = min(s[7] for s in loads if s[7] > 1 and s[7] & (s[7] - 1) == 0)
    relro = [s for s in segments if s[0] == 0x6474e552]
    # Bionic lowers alignment for a RELRO prefix followed by writable data.
    if len(relro) == 1:
        r = relro[0]
        if any(s[3] == r[3] and s[6] > r[6] for s in loads):
            end = r[3] + r[6]
            alignment = end & -end
            if alignment >= 4096: minimum = min(minimum, alignment)
    return {'elfClass': 64, 'machine': machine, 'loadAlignments': [s[7] for s in loads],
            'androidEffectiveAlignment': minimum, 'native16kbLayout': minimum >= 16384, 'needed': names}

def tool_inventory():
    paths = [IL2CPP, IL2CPP.with_name('Unity.IL2CPP.dll'), IL2CPP.with_name('Unity.IL2CPP.Building.dll'),
             NDK / 'source.properties', NDK / 'toolchains/llvm/prebuilt/windows-x86_64/bin/clang++.exe',
             BASELIB / 'baselib.a', NATIVE / 'libmain.so', NATIVE / 'libunity.so',
             DATA / 'Tools/MapFileParser/MapFileParser.exe', INSPECTOR, AOT_PATCHER, AOT_HELPER]
    return [describe(path) for path in paths]

def replacement(name):
    for folder, kind in ((AOT, 'exact_android_il2cpp_engine'), (BCL, 'exact_unityaot_bcl'),
                         (BCL / 'Facades', 'exact_unityaot_facade'), (SOURCE, 'accepted_game_or_package')):
        path = folder / name
        if path.is_file(): return path, kind
    raise FileNotFoundError('Unresolved managed dependency: ' + name)

def select_inputs(build, reject_diagnostics=True, android_runtime=None):
    selected = {path.name: replacement(path.name) for path in SOURCE.glob('*.dll') if path.name not in EXCLUDED}
    if android_runtime:
        runtime = android_runtime.resolve(strict=True)
        require(runtime.is_relative_to(ROOT / 'work') and runtime.name == 'ClosingShift.Android.dll', 'Runtime override must be a reviewed work helper')
        selected[runtime.name] = runtime, 'reviewed_qa_android_runtime'
    while True:
        audit_inputs = build / 'audit/input-paths.json'
        audit_output = build / 'audit/managed-metadata.json'
        save(audit_inputs, [str(path) for path, _ in selected.values()])
        run(['dotnet', INSPECTOR, audit_inputs, audit_output], build / 'audit/inspect.log')
        audit = json.loads(audit_output.read_text(encoding='utf-8'))
        additions = set(reference + '.dll' for module in audit for reference in module['references']) - selected.keys()
        if reject_diagnostics:
            require(not (additions & EXCLUDED), 'Game/helper still directly references excluded diagnostics; remove its diagnostic hook after control freeze')
        additions -= EXCLUDED
        if not additions: break
        for name in sorted(additions): selected[name] = replacement(name)
    return selected, audit

def commands(build, extra_types=None):
    common = [str(IL2CPP), '--platform=Android', '--architecture=ARM64', '--dotnetprofile=unityaot', '--jobs=4',
              '--directory=' + str(build / 'Managed'), '--generatedcppdir=' + str(build / 'cpp'),
              '--data-folder=' + str(build / 'data'), '--cachedirectory=' + str(build / 'cache')]
    convert = common + ['--convert-to-cpp', '--emit-null-checks', '--enable-array-bounds-check',
                        '--enable-divide-by-zero-check', '--symbols-folder=' + str(build / 'symbols')]
    if extra_types: convert.append('--extra-types-file=' + str(extra_types))
    compile = common + ['--compile-cpp', '--libil2cpp-static', '--configuration=Release', '--outputpath=' + str(build / 'libil2cpp.so'),
                        '--tool-chain-path=' + str(NDK), '--baselib-directory=' + str(BASELIB),
                        '--map-file-parser=' + str(DATA / 'Tools/MapFileParser/MapFileParser.exe'),
                        '--linker-flags=-Wl,-z,max-page-size=16384,-z,common-page-size=16384']
    return {'convert': convert, 'compile': compile}

def verify_frozen(build):
    manifest = json.loads((build / 'inputs.json').read_text(encoding='utf-8'))
    require(manifest['format'] == 'closing-shift-arm64-il2cpp-v1' and manifest['controlsFrozenByInvoker'], 'Accepted frozen-control input manifest required')
    require(manifest['architecture'] == 'ARM64' and manifest['stripping'] is False, 'ARM64 unstripped policy differs')
    current = {path.name: sha(path) for path in SOURCE.glob('*.dll') if path.name not in EXCLUDED}
    require(current == manifest['acceptedCanonicalManaged'], 'Accepted canonical DLL set/hash changed after freeze')
    for row in manifest['tools'] + manifest['inputs']:
        require(sha(Path(row['path'])) == row['sha256'], 'Frozen input/tool changed: ' + row['path'])
    for row in manifest['inputs']:
        require(sha(build / 'Managed' / row['name']) == row.get('preparedSha256', row['sha256']), 'Prepared DLL changed')
    if manifest['extraTypes']:
        row = manifest['extraTypes']
        require(sha(Path(row['path'])) == row['sha256'], 'AOT extra-types changed after freeze')
    require(all('--architecture=ARM64' in manifest['commands'][stage] for stage in ('convert', 'compile')), 'Non-ARM64 stage rejected')
    return manifest

def prepare(args, build):
    require(args.controls_frozen, 'Prepare only after the root explicitly freezes accepted touch/gamepad controls; then pass --controls-frozen')
    require(not (build / 'inputs.json').exists() and not (build / 'Managed').exists(), 'Choose a fresh work build directory')
    require(REQUIRED <= {path.name for path in SOURCE.glob('*.dll')}, 'Not all accepted helper DLLs are staged yet: ' + ', '.join(sorted(REQUIRED - {p.name for p in SOURCE.glob('*.dll')})))
    tools = tool_inventory()
    selected, audit = select_inputs(build, android_runtime=args.android_runtime)
    (build / 'Managed').mkdir(parents=True)
    inputs = []
    for name, (path, kind) in sorted(selected.items()):
        item = {'name': name, 'kind': kind, **describe(path)}
        shutil.copyfile(path, build / 'Managed' / name)
        require(sha(build / 'Managed' / name) == item['sha256'], 'Prepared DLL copy differs')
        inputs.append(item)
    # Only work copies are adapted for AOT. Original gameplay definitions and
    # every canonical source DLL remain the accepted preimages.
    compat = build / 'aot-compat'
    run(['dotnet', AOT_PATCHER, build / 'Managed', compat, AOT_HELPER], build / 'audit/aot-compat.log')
    compatibility = json.loads((compat / 'manifest.json').read_text(encoding='utf-8'))
    require(compatibility['format'] == 'closing-shift-aot-compat-v1', 'Unexpected AOT compatibility output')
    for patch in compatibility['files']:
        item = next(item for item in inputs if item['name'] == patch['file'])
        require(item['sha256'] == patch['oldSha256'], 'AOT patch input differs from accepted original')
        shutil.copyfile(compat / patch['file'], build / 'Managed' / patch['file'])
        item['preparedSha256'] = patch['newSha256']
        require(sha(build / 'Managed' / patch['file']) == item['preparedSha256'], 'AOT patched DLL copy differs')
    shutil.copyfile(AOT_HELPER, build / 'Managed' / AOT_HELPER.name)
    inputs.append({'name': AOT_HELPER.name, 'kind': 'reviewed_aot_reflection_fallback', **describe(AOT_HELPER)})
    save(build / 'audit/prepared-input-paths.json', [str(build / 'Managed' / item['name']) for item in inputs])
    run(['dotnet', INSPECTOR, build / 'audit/prepared-input-paths.json', build / 'audit/prepared-metadata.json'], build / 'audit/prepared-inspect.log')
    prepared_audit = json.loads((build / 'audit/prepared-metadata.json').read_text(encoding='utf-8'))
    available = {module['name'] for module in prepared_audit}
    require(all(set(module['references']) <= available for module in prepared_audit), 'Prepared assembly dependency closure differs')
    extra = args.extra_types.resolve(strict=True) if args.extra_types else None
    if extra: require(extra.is_relative_to(ROOT / 'work'), 'Reviewed AOT extra-types must be a work artifact')
    manifest = {'format': 'closing-shift-arm64-il2cpp-v1', 'architecture': 'ARM64', 'controlsFrozenByInvoker': True,
        'stripping': False, 'roots': 'Every selected DLL is passed directly to IL2CPP; no UnityLinker or method stripping stage',
        'acceptedCanonicalManaged': {path.name: sha(path) for path in SOURCE.glob('*.dll') if path.name not in EXCLUDED},
        'excludedDiagnostics': sorted(EXCLUDED), 'tools': tools, 'inputs': inputs,
        'stockMetadataCache': new_cache_stamp(known_cache_dates()),
        'aotCompatibilityManifestSha256': sha(compat / 'manifest.json'), 'aotCompatibilityExistingMethodsPatched': 13,
        'extraTypes': describe(extra) if extra else None, 'commands': commands(build, extra),
        'native': {name: elf_info(NATIVE / name) for name in ('libmain.so', 'libunity.so')},
        'remainingRuntimeAudit': ['Reviewed existing reflection alternatives and exact callback delegates are applied to13 desktop emit-path methods; actual AOT runtime acceptance remains required',
            'MakeGenericType/MakeGenericMethod value-type instantiations require observed AOT roots and route tests',
            'Windows-only native plugin calls in audit/nativeCalls must stay unreachable or gain an Android implementation',
            '32-bit large-file helper must not be loaded in ARM64; OBB reads need device acceptance'],
        'dynamicCallSites': sum(len(module['dynamicCalls']) for module in audit),
        'genericMethodSpecSignaturesIncludingOpenParameters': sum(len(module['genericCalls']) for module in audit),
        'nativeCompilationStarted': False}
    save(build / 'inputs.json', manifest)
    verify_frozen(build)
    print('PREPARE_PASS: frozen unstripped ARM64 inputs; no C++ conversion or compilation executed')

def stage_scripting_assemblies(destination):
    require(destination.resolve().is_relative_to(ROOT / 'work'), 'Staged scripting list must stay under work')
    path = destination / 'assets/bin/Data/ScriptingAssemblies.json'
    before = describe(path)
    document = json.loads(path.read_text(encoding='utf-8'))
    require(set(document) == {'names', 'types'}, 'Unexpected scripting-assembly metadata fields')
    names, types = document['names'], document['types']
    require(len(names) == len(types) and len(names) == len(set(names)), 'Invalid scripting-assembly list')
    original_rows = list(zip(names, types))
    require(('ClosingShift.MobileRender.dll', 16) in original_rows, 'Accepted mobile-render assembly registration missing')
    rows = [(name, kind) for name, kind in original_rows if name not in EXCLUDED]
    helper = 'ClosingShift.AotCompat.dll'
    helper_rows = [row for row in rows if row[0] == helper]
    require(helper_rows in ([], [(helper, 16)]), 'Unexpected AOT helper scripting-assembly type')
    if not helper_rows: rows.append((helper, 16))
    require([(name, kind) for name, kind in rows if name != helper] ==
            [(name, kind) for name, kind in original_rows if name not in EXCLUDED and name != helper],
            'Unrelated scripting-assembly metadata changed')
    document['names'] = [name for name, _ in rows]
    document['types'] = [kind for _, kind in rows]
    if rows != original_rows:
        path.write_text(json.dumps(document, separators=(',', ':')) + '\n', encoding='utf-8')
    saved = json.loads(path.read_text(encoding='utf-8'))
    require(list(zip(saved['names'], saved['types'])) == rows, 'Persisted scripting-assembly metadata differs')
    return {'before': before, 'after': describe(path), 'rowsBefore': len(original_rows), 'rowsAfter': len(rows),
            'excludedDiagnostics': sorted(EXCLUDED), 'aotCompatType': 16, 'mobileRenderType': 16,
            'allOtherRowsPreserved': True}

def verify_runtime_data(build):
    converted = json.loads((build / 'convert-result.json').read_text(encoding='utf-8'))
    require(converted['inputsManifestSha256'] == sha(build / 'inputs.json'), 'Runtime data belongs to different inputs')
    actual = sorted(str(path.resolve()) for path in (build / 'data').rglob('*') if path.is_file())
    require(actual == sorted(row['path'] for row in converted['runtimeData']), 'Generated runtime-data file set changed')
    for row in converted['runtimeData']:
        require(sha(Path(row['path'])) == row['sha256'], 'Generated runtime data changed: ' + row['path'])
    return converted['runtimeData']

def stage_obb_source(build, destination, manifest_path):
    """Stock OBB Managed overlay; game bulk is linked, generated AOT data copied."""
    require(destination.resolve().is_relative_to(ROOT / 'work') and not destination.exists(), 'Fresh work OBB source required')
    rows = verify_runtime_data(build)
    source = ROOT / 'app/obb'
    require(source.is_dir() and not source.is_symlink(), 'Accepted canonical OBB source missing or symlinked')
    android = '{http://schemas.android.com/apk/res/android}'
    manifest = ET.parse(manifest_path).getroot()
    package, version = manifest.get('package'), manifest.get(android + 'versionCode')
    require(package and version and version.isdecimal(), 'Literal APK package/version required')
    markers = [node.get(android + 'value') for node in manifest.iter('meta-data') if node.get(android + 'name') == 'unity.build-id']
    require(len(markers) == 1 and markers[0], 'One Unity build-id required')
    build_id = markers[0]
    marker = source / 'assets' / build_id
    require(marker.is_file() and marker.stat().st_size == 0, 'Canonical OBB marker differs from staged APK')
    require(not (source / 'assets/bin/Data/Managed').exists(), 'Canonical bulk already contains Managed data; explicit ownership review required')
    directories = sorted((p for p in source.rglob('*') if p.is_dir()), key=lambda p: len(p.parts))
    require(not any(p.is_symlink() for p in source.rglob('*')), 'Symlink in canonical OBB source')
    files = sorted(p for p in source.rglob('*') if p.is_file())
    require(files and all(p.resolve().is_relative_to(source.resolve()) for p in files), 'Invalid canonical OBB files')
    destination.mkdir(parents=True)
    for directory in directories: (destination / directory.relative_to(source)).mkdir()
    bulk = []
    for path in files:
        target = destination / path.relative_to(source)
        before = describe(path)
        os.link(path, target)
        require(os.path.samefile(path, target) and target.stat().st_size == before['bytes'], 'OBB hardlink differs')
        bulk.append({'source': before, 'stagedPath': str(target.resolve()), 'hardlinked': True})
    managed = destination / 'assets/bin/Data/Managed'
    shutil.copytree(build / 'data', managed)
    generated = []
    for row in rows:
        target = managed / Path(row['path']).relative_to(build / 'data')
        require(sha(target) == row['sha256'], 'OBB generated-data copy differs')
        generated.append(describe(target))
    require(not list(managed.rglob('*.dll')), 'JIT DLLs unexpectedly present in IL2CPP Managed overlay')
    require((managed / 'Metadata/global-metadata.dat').is_file(), 'OBB metadata overlay missing')
    require(len(files) + len(generated) == sum(1 for p in destination.rglob('*') if p.is_file()), 'Unexpected OBB source file set')
    for item in bulk:
        require(sha(Path(item['stagedPath'])) == item['source']['sha256'], 'Canonical bulk changed during OBB staging')
    return {'path': str(destination.resolve()), 'package': package, 'versionCode': int(version), 'buildId': build_id,
            'bulk': bulk, 'generatedManagedData': generated,
            'generatedManagedBytes': sum(row['bytes'] for row in generated),
            'stockManagedExtractionOnly': True, 'bulkDirectObbReads': True,
            'excludedFromApkPayload': 'Top-level obb directory; APK packager includes only assets/ and lib/'}

def stage_apk_source(destination):
    """Link canonical bulk into this build's APK; no game-data extraction or duplicate Managed overlay."""
    require(destination.resolve().is_relative_to(ROOT / 'work') and destination.is_dir(), 'Expected a staged work APK layout')
    source = ROOT / 'app/obb'
    require(source.is_dir() and not source.is_symlink(), 'Accepted canonical bulk source missing or symlinked')
    android = '{http://schemas.android.com/apk/res/android}'
    manifest = ET.parse(destination / 'AndroidManifest.xml').getroot()
    package, version = manifest.get('package'), manifest.get(android + 'versionCode')
    require(package and version and version.isdecimal(), 'Literal APK package/version required')
    markers = [node.get(android + 'value') for node in manifest.iter('meta-data') if node.get(android + 'name') == 'unity.build-id']
    require(len(markers) == 1 and markers[0], 'One Unity build-id required')
    marker = source / 'assets' / markers[0]
    require(marker.is_file() and marker.stat().st_size == 0, 'Canonical bulk marker differs from staged APK')
    require(not (source / 'assets/bin/Data/Managed').exists(), 'Canonical bulk unexpectedly owns generated Managed data')
    paths = sorted(source.rglob('*'))
    require(not any(path.is_symlink() for path in paths), 'Symlink in canonical bulk source')
    files = [path for path in paths if path.is_file()]
    require(files and all(path.resolve().is_relative_to(source.resolve()) for path in files), 'Invalid canonical bulk files')
    require(all(not (destination / path.relative_to(source)).exists() for path in files), 'Canonical bulk collides with APK inputs')
    bulk = []
    for path in files:
        before = describe(path)
        target = destination / path.relative_to(source)
        target.parent.mkdir(parents=True, exist_ok=True)
        os.link(path, target)
        require(os.path.samefile(path, target) and sha(target) == before['sha256'], 'APK bulk hardlink differs')
        bulk.append({'source': before, 'stagedPath': str(target.resolve()), 'hardlinked': True})
    return {'path': str(destination.resolve()), 'package': package, 'versionCode': int(version),
            'buildId': markers[0], 'bulk': bulk, 'bulkDirectApkReads': True,
            'gameDataExtraction': False, 'streamAliases': 'build_apk payload_entries: .resS.resource; compressed native-whitelist 1MiB parts, legacy stored resS 512MiB'}

def stage_app(build):
    manifest = verify_frozen(build)
    require((build / 'compile-result.json').is_file(), 'Compile must succeed before app staging')
    compiled = json.loads((build / 'compile-result.json').read_text(encoding='utf-8'))
    require(compiled['inputsManifestSha256'] == sha(build / 'inputs.json')
            and compiled['library']['sha256'] == sha(build / 'libil2cpp.so'), 'Compiled library/input preimage changed')
    require(not (build / 'stage-app-result.json').exists(), 'This build was already staged; existing receipt/layout is never overwritten')
    destination = build / 'app'
    require(not destination.exists(), 'Fresh per-build app directory required; existing layout is never overwritten')
    verify_runtime_data(build)
    converted = json.loads((build / 'convert-result.json').read_text(encoding='utf-8'))
    identity = cache_identity(manifest, converted, compiled)
    source = ROOT / 'app'
    # Keep canonical ownership unchanged; the work layout merges small inputs and bulk.
    for name in ('AndroidManifest.xml', 'res', 'src', 'assets'):
        path = source / name
        if path.is_dir():
            def omit_only_jit_managed(directory, children):
                return ['Managed'] if Path(directory).resolve() == SOURCE.parent.resolve() else []
            shutil.copytree(path, destination / name, ignore=omit_only_jit_managed if name == 'assets' else None)
        elif path.is_file():
            destination.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, destination / name)
    for path in (destination / 'src').rglob('*.java'):
        text = path.read_text(encoding='utf-8')
        needle = 'int ioResult = com.chillasart.theclosingshift.port.LargeObbIO.install(getCacheDir().getAbsolutePath());'
        if needle in text:
            # The final layout contains only ARM64; skip class initialization and
            # the ELF32 GOT helper entirely. Reject an unexpected launcher shape.
            following = 'if (ioResult != 0) throw new IllegalStateException("Large OBB IO initialization failed: " + ioResult);'
            require(text.count(needle) == 1 and text.count(following) == 1, 'Launcher IO hook shape changed')
            text = text.replace(needle, '// ARM64 libc long/off_t are64-bit; no ELF32 LargeObbIO shim.').replace(following, '')
            path.write_text(text, encoding='utf-8')
    helper = destination / 'src/com/chillasart/theclosingshift/port/LargeObbIO.java'
    if helper.exists(): helper.unlink()
    require(not any('LargeObbIO.install' in path.read_text(encoding='utf-8') for path in (destination / 'src').rglob('*.java')), 'Residual 32-bit IO hook')
    native = destination / 'lib/arm64-v8a'
    native.mkdir(parents=True)
    for name, path in [('libmain.so', NATIVE / 'libmain.so'), ('libunity.so', NATIVE / 'libunity.so'), ('libil2cpp.so', build / 'libil2cpp.so')]:
        elf_info(path)
        shutil.copyfile(path, native / name)
    dependencies = elf_info(build / 'libil2cpp.so')['needed']
    if 'libc++_shared.so' in dependencies:
        runtime = NDK / 'sources/cxx-stl/llvm-libc++/libs/arm64-v8a/libc++_shared.so'
        elf_info(runtime)
        shutil.copyfile(runtime, native / runtime.name)
    metadata = build / 'data/Metadata/global-metadata.dat'
    require(metadata.is_file(), 'IL2CPP metadata output missing')
    shutil.copytree(build / 'data', destination / 'assets/bin/Data/Managed')
    require(not list((destination / 'assets/bin/Data/Managed').rglob('*.dll')), 'JIT assemblies unexpectedly packaged')
    require({p.name for p in (destination / 'lib').iterdir()} == {'arm64-v8a'}, 'Output must contain only ARM64')
    scripting = stage_scripting_assemblies(destination)
    stock_cache = stage_boot_config(build, destination, identity)
    assets = stage_apk_source(destination)
    save(build / 'stage-app-result.json', {'layout': str(destination), 'inputsManifestSha256': sha(build / 'inputs.json'),
          'native': [describe(path) for path in native.glob('*.so')], 'metadata': describe(destination / 'assets/bin/Data/Managed/Metadata/global-metadata.dat'),
          'scriptingAssemblies': scripting, 'stockMetadataCache': stock_cache,
          'apkInputs': [describe(path) for path in sorted(destination.rglob('*')) if path.is_file() and not path.is_relative_to(destination / 'obb')],
          'dataContainer': 'apk', 'assetSource': assets, 'canonicalAppUnchanged': True})
    print('STAGE_APP_PASS ' + str(destination))

def build_obb(build):
    staged = json.loads((build / 'stage-app-result.json').read_text(encoding='utf-8'))
    require(staged.get('dataContainer', 'obb') == 'obb', 'This is a single-APK layout; build_apk.py --compress packages its assets directly')
    compiled = json.loads((build / 'compile-result.json').read_text(encoding='utf-8'))
    converted = json.loads((build / 'convert-result.json').read_text(encoding='utf-8'))
    manifest = json.loads((build / 'inputs.json').read_text(encoding='utf-8'))
    identity = cache_identity(manifest, converted, compiled)
    require(all(staged['stockMetadataCache'].get(key) == value for key, value in identity.items()),
            'Staged stock metadata-cache identity differs')
    require(staged['inputsManifestSha256'] == compiled['inputsManifestSha256'] == sha(build / 'inputs.json'), 'Staged native/input build identity differs')
    layout = Path(staged['layout'])
    require(layout == build / 'app', 'Expected per-build app layout with stock OBB overlay')
    source = staged['obbSource']
    require(Path(source['path']) == layout / 'obb', 'Unexpected staged OBB source')
    actual_apk_files = sorted(str(path.resolve()) for path in layout.rglob('*') if path.is_file() and not path.is_relative_to(layout / 'obb'))
    require(actual_apk_files == sorted(row['path'] for row in staged['apkInputs']), 'Staged APK input set changed')
    for row in staged['apkInputs'] + source['generatedManagedData']:
        require(sha(Path(row['path'])) == row['sha256'], 'Staged input changed: ' + row['path'])
    for row in source['bulk']:
        require(sha(Path(row['stagedPath'])) == row['source']['sha256'], 'Staged OBB bulk changed')
    expected_obb_files = {row['stagedPath'] for row in source['bulk']} | {row['path'] for row in source['generatedManagedData']}
    require(expected_obb_files == {str(path.resolve()) for path in (layout / 'obb').rglob('*') if path.is_file()}, 'Staged OBB file set changed')
    require(staged['metadata']['sha256'] == converted['metadata']['sha256']
            == compiled.get('metadataSha256', converted['metadata']['sha256']), 'Metadata/native identity differs')
    verify_runtime_data(build)
    for row in staged['native']: elf_info(Path(row['path']))
    require(sha(layout / 'lib/arm64-v8a/libil2cpp.so') == compiled['library']['sha256'], 'Staged compiled native library differs')
    output = build / 'package/obb'
    require(not output.exists(), 'Fresh per-build OBB package output required')
    packager_path = ROOT / 'scripts/build_obb.py'
    packager_hash = sha(packager_path)
    spec = importlib.util.spec_from_file_location('closing_shift_stock_obb_packager', packager_path)
    packager = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(packager)
    result = packager.build_archives(layout / 'obb', output, source['package'], str(source['versionCode']), source['buildId'])
    require(len(result['caches']) == 1, 'Final ARM64 layout requires one OBB cache')
    require(sha(packager_path) == packager_hash, 'OBB packager changed during build')
    save(build / 'obb-build-result.json', {'inputsManifestSha256': sha(build / 'inputs.json'),
         'stagingReceiptSha256': sha(build / 'stage-app-result.json'), 'packagerSha256': packager_hash,
         'librarySha256': compiled['library']['sha256'], 'metadataSha256': staged['metadata']['sha256'],
         'stockMetadataCache': identity,
         'obbManifest': describe(output / 'obb-manifest.json'), 'caches': [describe(Path(row['file'])) for row in result['caches']],
         'apkBuilt': False, 'deviceTouched': False})
    print('OBB_BUILD_PASS one cache with stock IL2CPP Managed data; APK/device unchanged')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    stage = parser.add_mutually_exclusive_group()
    for name in ('plan', 'prepare', 'convert', 'compile', 'stage-app', 'build-obb', 'self-check'): stage.add_argument('--' + name, action='store_true')
    parser.add_argument('--build-dir', type=Path, default=ROOT / 'work/a64')
    parser.add_argument('--controls-frozen', action='store_true')
    parser.add_argument('--android-runtime', type=Path, help='Reviewed QA helper; canonical production DLLs remain frozen')
    parser.add_argument('--extra-types', type=Path)
    args = parser.parse_args()
    build = args.build_dir.resolve()
    require(build.is_relative_to(ROOT / 'work') and build != ROOT / 'work', 'Build artifacts must stay in a dedicated work subdirectory')
    if args.self_check:
        command = commands(build)
        require(all('--architecture=ARM64' in value for value in command.values()), 'Architecture guard')
        require(not any('ARMv7' in item or 'ARM32' in item for value in command.values() for item in value), 'Unexpected architecture')
        for name in ('libmain.so', 'libunity.so'): elf_info(NATIVE / name)
        print('SELF_CHECK_PASS ARM64 commands, exact local tool paths and ELF64 machine guards; no game conversion executed')
    elif args.prepare: prepare(args, build)
    elif args.convert:
        manifest = verify_frozen(build)
        validate_cache_stamp(manifest.get('stockMetadataCache'))
        require(not (build / 'convert-result.json').exists(), 'Conversion result already exists')
        run(manifest['commands']['convert'], build / 'convert.log')
        metadata = build / 'data/Metadata/global-metadata.dat'
        require(metadata.is_file() and struct.unpack_from('<I', metadata.read_bytes())[0] == 0xfab11baf, 'Generated metadata missing/invalid')
        registration = list((build / 'cpp').glob('*Registration*.*'))
        text = '\n'.join(path.read_text(encoding='utf-8', errors='replace') for path in registration if path.suffix in ('.c', '.cpp'))
        require('g_CodeRegistration' in text and 'g_MetadataRegistration' in text, 'Generated code/metadata registration missing')
        metadata_receipt = describe(metadata)
        save(build / 'convert-result.json', {'inputsManifestSha256': sha(build / 'inputs.json'), 'metadata': metadata_receipt,
              'stockMetadataCache': {**manifest['stockMetadataCache'], 'metadataSha256': metadata_receipt['sha256']},
              'registration': [describe(path) for path in registration],
              'generatedInputs': [describe(path) for path in generated_inputs(build)],
              'runtimeData': [describe(path) for path in sorted((build / 'data').rglob('*')) if path.is_file()], 'architecture': 'ARM64'})
        print('CONVERT_PASS ARM64 generated registration and metadata')
    elif args.compile:
        manifest = verify_frozen(build)
        require((build / 'convert-result.json').is_file(), 'Conversion must succeed first')
        converted = json.loads((build / 'convert-result.json').read_text(encoding='utf-8'))
        require(converted['inputsManifestSha256'] == sha(build / 'inputs.json'), 'Conversion belongs to different inputs')
        require([str(path.resolve()) for path in generated_inputs(build)] == [row['path'] for row in converted['generatedInputs']], 'Generated C++/header file set changed')
        for row in converted['generatedInputs'] + converted['runtimeData']:
            require(sha(Path(row['path'])) == row['sha256'], 'Generated input changed')
        # Unity2020 AndroidProjectExportGradle.GetIL2CppBuildSetup includes
        # --libil2cpp-static: the runtime and generated code belong in one .so.
        # Keep an already-converted input manifest immutable; permit only this
        # exact correction to the earlier standalone command, not arbitrary flags.
        effective_command = commands(build)['compile']
        recorded_command = manifest['commands']['compile']
        require(recorded_command in (effective_command,
                [arg for arg in effective_command if arg != '--libil2cpp-static']),
                'Recorded compile command differs beyond reviewed Android static-runtime correction')
        if (build / 'compile.log').is_file():
            prior_logs = sorted(build.glob('compile-attempt-*.log'))
            previous = build / ('compile-attempt-%03d.log' % (len(prior_logs) + 1))
            require(not previous.exists(), 'Compile-attempt log collision')
            shutil.copyfile(build / 'compile.log', previous)
        save(build / 'compile-command.json', {'inputsManifestSha256': sha(build / 'inputs.json'),
             'command': effective_command, 'recordedCommand': recorded_command,
             'correction': 'Exact Unity2020 AndroidProjectExportGradle command requires --libil2cpp-static; frozen DLLs and generated code are unchanged'})
        run(effective_command, build / 'compile.log')
        library = build / 'libil2cpp.so'
        save(build / 'compile-result.json', {'library': describe(library), 'elf': elf_info(library), 'architecture': 'ARM64',
             'inputsManifestSha256': sha(build / 'inputs.json'),
             'metadataSha256': converted['metadata']['sha256'],
             'compileCommandSha256': sha(build / 'compile-command.json'), 'deviceValidated': False})
        print('COMPILE_PASS ELF64 AArch64 libil2cpp.so')
    elif args.stage_app: stage_app(build)
    elif args.build_obb: build_obb(build)
    else:
        inventory = tool_inventory()
        selected, audit = select_inputs(build, reject_diagnostics=False)
        plan = {'mode': 'PLAN_ONLY', 'controlsFrozen': False, 'gameConversionExecuted': False, 'architecture': 'ARM64',
                'tools': inventory, 'inputs': [{'name': name, 'kind': kind, **describe(path)} for name, (path, kind) in sorted(selected.items())],
                'missingAcceptedHelpers': sorted(REQUIRED - {path.name for path in SOURCE.glob('*.dll')}),
                'diagnosticReferencesToRemove': [module['name'] for module in audit if any(name[:-4] in module['references'] for name in EXCLUDED)],
                'dynamicCallSites': sum(len(module['dynamicCalls']) for module in audit),
                'commandsAfterControlFreeze': commands(build), 'forbidden': 'ARM32 IL2CPP is never an option'}
        save(build / 'plan.json', plan)
        print(json.dumps({key: plan[key] for key in ('mode', 'controlsFrozen', 'architecture', 'missingAcceptedHelpers', 'diagnosticReferencesToRemove', 'dynamicCallSites')}))

if __name__ == '__main__': main()
