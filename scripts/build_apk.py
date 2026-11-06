#!/usr/bin/env python3
"""Package a canonical Android tree with pinned tools, without Gradle or asset extraction."""

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

from build_obb import STREAM_SEGMENT_BYTES, virtual_entries


MACHINE_CONFIG = Path.home() / ".agents/skills/adapt-arm32-game/machine_config.json"
ANDROID_NS = "{http://schemas.android.com/apk/res/android}"
# Android APKs use ZIP32; Python's conservative 2-GiB threshold is below the format limit.
ZIP32_LIMIT = (1 << 32) - 1
zipfile.ZIP64_LIMIT = ZIP32_LIMIT
COMPRESSED_SEGMENT_BYTES = 1024 * 1024
# Android MediaExtractor needs an uncompressed whole APK entry for its file descriptor.
WHOLE_STORED_VIDEO_ASSETS = frozenset((
    "assets/bin/Data/sharedassets0.resource",
    "assets/bin/Data/sharedassets1.resource",
))
WHOLE_STORED_STARTUP_ASSETS = (
    "assets/bin/Data/globalgamemanagers",
    "assets/bin/Data/globalgamemanagers.assets",
    "assets/bin/Data/level0",
    "assets/bin/Data/level1",
    "assets/bin/Data/resources.assets",
    "assets/bin/Data/sharedassets0.assets",
    "assets/bin/Data/sharedassets1.assets",
    "assets/bin/Data/unity default resources",
    "assets/bin/Data/Resources/unity default resources",
    "assets/bin/Data/Resources/unity_builtin_extra",
    "assets/bin/Data/Managed/Metadata/global-metadata.dat",
    "assets/bin/Data/resources.resource",
)


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(args, log_path):
    result = subprocess.run([str(arg) for arg in args], capture_output=True, text=True,
                            encoding="utf-8", errors="replace")
    Path(log_path).write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {args[0]}\n"
                           f"{(result.stdout + result.stderr)[-5000:]}\nLog: {log_path}")
    return result.stdout + result.stderr


def manifest_identity(path):
    root = ET.parse(path).getroot()
    package = root.get("package")
    version = root.get(ANDROID_NS + "versionCode")
    if not package or not re.fullmatch(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+", package):
        raise ValueError(f"Missing or invalid manifest package: {path}")
    if not version or not version.isdecimal():
        raise ValueError(f"Manifest needs a literal versionCode: {path}")
    return package, version, root.get("split")


def split_data_file(name):
    basename = name.rsplit("/", 1)[-1]
    return (name.startswith("assets/bin/Data/") and not name.startswith("assets/bin/Data/Managed/")
            and (basename.startswith("level") or basename.endswith((".assets", ".resource"))))


def packing_policy(compress):
    result = {"compression": "deflate" if compress else "stored",
              "dataSplit": "unity2020-native" if compress else "resS-only",
              "segmentBytes": COMPRESSED_SEGMENT_BYTES if compress else STREAM_SEGMENT_BYTES}
    if compress:
        result["wholeStoredVideoAssets"] = sorted(WHOLE_STORED_VIDEO_ASSETS)
        result["wholeStoredStartupAssets"] = list(WHOLE_STORED_STARTUP_ASSETS)
    return result


def payload_compression(name, compress):
    return zipfile.ZIP_DEFLATED if compress and name not in WHOLE_STORED_VIDEO_ASSETS and name not in WHOLE_STORED_STARTUP_ASSETS else zipfile.ZIP_STORED


def payload_entries(files, source, compress=False):
    if not compress:
        return virtual_entries(files, source)
    entries = []
    for path in files:
        size = path.stat().st_size
        name = path.relative_to(source).as_posix() + (".resource" if path.name.endswith(".resS") else "")
        split = name not in WHOLE_STORED_VIDEO_ASSETS and name not in WHOLE_STORED_STARTUP_ASSETS and split_data_file(name) and size > COMPRESSED_SEGMENT_BYTES
        # Exact Unity2020 AndroidSplitFile accepts level*, .assets and .resource; bootstrap names stay whole.
        for index, offset in enumerate(range(0, size, COMPRESSED_SEGMENT_BYTES) if split else (0,)):
            entries.append({"source_path": path, "name": name + ".split" + str(index) if split else name,
                            "range_offset": offset, "bytes": min(COMPRESSED_SEGMENT_BYTES, size-offset) if split else size})
    return entries


def payload_files(source, compress=False):
    result = []
    for folder in ("assets", "lib"):
        root = source / folder
        if not root.exists():
            continue
        for path in sorted(root.rglob("*")):
            if path.is_symlink():
                raise ValueError(f"Symlinks are not packaged: {path}")
            if path.is_file():
                result.append(path)
    return payload_entries(result, source, compress)


def add_payload(archive, files, compress=False):
    seen = set(archive.namelist())
    result = []
    for entry in files:
        path, name, size = entry["source_path"], entry["name"], entry["bytes"]
        if name in seen or name.startswith("/") or ".." in Path(name).parts:
            raise ValueError(f"Duplicate or unsafe APK entry: {name}")
        if size >= ZIP32_LIMIT or archive.fp.tell() + size + len(name) + 128 >= ZIP32_LIMIT:
            raise ValueError(f"ZIP32 APK size exceeded at {name}; use smaller asset splits")
        metadata = zipfile.ZipInfo.from_file(path, name)
        metadata.file_size = size
        metadata.compress_type = payload_compression(name, compress)
        remaining, digest = size, hashlib.sha256()
        with path.open("rb") as source, archive.open(metadata, "w", force_zip64=False) as target:
            source.seek(entry["range_offset"])
            while remaining:
                data = source.read(min(remaining, 1024 * 1024))
                if not data:
                    raise ValueError(f"Truncated APK input range: {path}")
                target.write(data)
                digest.update(data)
                remaining -= len(data)
        result.append({"name": name, "bytes": size, "sha256": digest.hexdigest(),
                       "compression": "deflate" if metadata.compress_type == zipfile.ZIP_DEFLATED else "stored"})
        seen.add(name)
    return result


def package_target(source, destination, dex_files, tools, java, signer, android_jar, compress=False):
    identity = manifest_identity(source / "AndroidManifest.xml")
    if compress and any((source / "lib").rglob("*.so")):
        application = ET.parse(source / "AndroidManifest.xml").getroot().find("application")
        if application is None or application.get(ANDROID_NS + "extractNativeLibs") != "true":
            raise ValueError("Compressed native libraries require android:extractNativeLibs=\"true\"")
    scratch = destination.parent / ("_apkbuild_" + destination.stem)
    scratch.mkdir(parents=True, exist_ok=True)
    manifest_apk = scratch / "manifest.apk"
    unsigned = scratch / "unsigned.apk"
    aligned = scratch / "aligned.apk"
    signed = scratch / "signed.apk"
    compiled_res = scratch / "resources.zip"
    link_args = [tools / "aapt2.exe", "link", "-I", android_jar,
                 "--manifest", source / "AndroidManifest.xml", "-o", manifest_apk]
    if (source / "res").is_dir():
        run([tools / "aapt2.exe", "compile", "--dir", source / "res", "-o", compiled_res],
            scratch / "aapt-compile.log")
        link_args.append(compiled_res)
    run(link_args, scratch / "aapt-link.log")
    shutil.copyfile(manifest_apk, unsigned)
    with zipfile.ZipFile(unsigned, "a", allowZip64=False) as archive:
        entries = payload_files(source, compress) + (payload_entries(dex_files, dex_files[0].parent, compress) if dex_files else [])
        payload = add_payload(archive, entries, compress)
    run([tools / "zipalign.exe", "-f", "-P", "16", "4", unsigned, aligned],
        scratch / "zipalign.log")
    run([java, "-jar", signer["jar"], "sign", "--key", signer["key"],
         "--cert", signer["cert"], "--v1-signing-enabled", "true",
         "--v2-signing-enabled", "true", "--v3-signing-enabled", "false",
         "--out", signed, aligned],
        scratch / "sign.log")
    verification = run([java, "-jar", signer["jar"], "verify", "--verbose", "--print-certs", signed],
                       scratch / "verify-signature.log")
    cert_match = re.search(r"certificate SHA-256 digest:\s*([0-9a-fA-F:]+)", verification)
    if not cert_match or cert_match[1].replace(":", "").lower() != signer["expected"]:
        raise RuntimeError(f"Unexpected signing certificate: {scratch / 'verify-signature.log'}")
    run([tools / "zipalign.exe", "-c", "-P", "16", "4", signed], scratch / "verify-alignment.log")
    badging = run([tools / "aapt2.exe", "dump", "badging", signed], scratch / "badging.txt")
    if f"name='{identity[0]}'" not in badging or f"versionCode='{identity[1]}'" not in badging:
        raise RuntimeError("Compiled APK package/version differs from source manifest")
    with zipfile.ZipFile(signed) as archive:
        if any(item.extract_version >= 45 for item in archive.infolist()):
            raise RuntimeError("ZIP64 entry found in output APK")
        if len(archive.namelist()) != len(set(archive.namelist())):
            raise RuntimeError("Duplicate output APK entries")
        for entry in payload:
            metadata = archive.getinfo(entry["name"])
            if metadata.file_size != entry["bytes"] or metadata.compress_type != payload_compression(entry["name"], compress):
                raise RuntimeError("APK payload size/compression differs: " + entry["name"])
            with archive.open(metadata) as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != entry["sha256"]:
                    raise RuntimeError("Signed APK payload differs: " + entry["name"])
            entry["compressedBytes"] = metadata.compress_size
    os.replace(signed, destination)
    return {"path": str(destination), "sha256": sha256(destination),
            "bytes": destination.stat().st_size, "package": identity[0],
            "version_code": identity[1], "split": identity[2], "payload": payload, "packingPolicy": packing_policy(compress),
            "payloadBytes": sum(entry["bytes"] for entry in payload),
            "compressedPayloadBytes": sum(entry["compressedBytes"] for entry in payload)}


def self_test():
    import tempfile
    with tempfile.TemporaryDirectory(prefix="closing-shift-apk-test-") as directory:
        root = Path(directory)
        (root / "assets").mkdir()
        (root / "assets" / "sample").write_bytes(b"direct asset")
        (root / "AndroidManifest.xml").write_text(
            '<manifest xmlns:android="http://schemas.android.com/apk/res/android" '
            'package="local.closingshift" android:versionCode="1"/>', encoding="utf-8")
        assert manifest_identity(root / "AndroidManifest.xml") == ("local.closingshift", "1", None)
        files = payload_files(root)
        assert len(files) == 1 and files[0]["name"] == "assets/sample"
        with zipfile.ZipFile(root / "sample.zip", "w", allowZip64=False) as archive:
            add_payload(archive, files)
            try:
                add_payload(archive, files)
            except ValueError:
                pass
            else:
                raise AssertionError("duplicate payload accepted")
        with zipfile.ZipFile(root / "sample.zip") as archive:
            assert archive.read("assets/sample") == b"direct asset"
            assert archive.getinfo("assets/sample").compress_type == zipfile.ZIP_STORED
        (root / "lib/arm64-v8a").mkdir(parents=True)
        (root / "lib/arm64-v8a/libtest.so").write_bytes(b"native" * 1000)
        stream = root / "assets/large.resS"
        stream.write_bytes(bytes(range(251)) * 17)
        entries = virtual_entries([stream], root, segment_bytes=1024)
        assert len(entries) == 5 and entries[0]["name"] == "assets/large.resS.resource.split0"
        entries += [entry for entry in payload_files(root) if entry["source_path"] != stream]
        with zipfile.ZipFile(root / "compressed.zip", "w", allowZip64=False) as archive:
            report = add_payload(archive, entries, compress=True)
        with zipfile.ZipFile(root / "compressed.zip") as archive:
            assert b"".join(archive.read(entry["name"]) for entry in entries[:5]) == stream.read_bytes()
            assert archive.read("lib/arm64-v8a/libtest.so") == b"native" * 1000
            assert all(entry.compress_type == zipfile.ZIP_DEFLATED and entry.extract_version < 45 for entry in archive.infolist())
            assert all(hashlib.sha256(archive.read(entry["name"])).hexdigest() == entry["sha256"] for entry in report)
        empty = root / "assets/empty.resS"
        empty.write_bytes(b"")
        assert virtual_entries([empty], root)[0]["bytes"] == 0
        for name in ("large.assets", "level0", "level2", "large.resS", "audio.resource", "unity default resources",
                     "globalgamemanagers", "Resources/unity_builtin_extra", "Managed/Metadata/global-metadata.dat", "Managed/test.assets"):
            path = root / "assets/bin/Data" / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"a" * (4236752 if name == "unity default resources" else COMPRESSED_SEGMENT_BYTES + 7))
            compressed = payload_entries([path], root, compress=True)
            legal = name in ("large.assets", "level2", "large.resS", "audio.resource")
            assert len(compressed) == (2 if legal else 1), name
            assert sum(row["bytes"] for row in compressed) == path.stat().st_size
            assert payload_entries([path], root) == virtual_entries([path], root), "Legacy stored policy changed"
        native = root / "lib/arm64-v8a/large.so"
        native.write_bytes(b"n" * (COMPRESSED_SEGMENT_BYTES + 7))
        assert len(payload_entries([native], root, compress=True)) == 1
        dex = root / "classes.dex"
        dex.write_bytes(b"d" * (COMPRESSED_SEGMENT_BYTES + 7))
        assert len(payload_entries([dex], root, compress=True)) == 1
        video_entries = []
        for name in sorted(WHOLE_STORED_VIDEO_ASSETS):
            video = root / name
            video.parent.mkdir(parents=True, exist_ok=True)
            video.write_bytes(b"v" * (COMPRESSED_SEGMENT_BYTES + 7))
            entries = payload_entries([video], root, compress=True)
            assert len(entries) == 1 and entries[0]["name"] == name and entries[0]["range_offset"] == 0
            video_entries += entries
        with zipfile.ZipFile(root / "videos.zip", "w", allowZip64=False) as archive:
            report = add_payload(archive, video_entries, compress=True)
        with zipfile.ZipFile(root / "videos.zip") as archive:
            assert all(info.compress_type == zipfile.ZIP_STORED for info in archive.infolist())
            assert all(row["compression"] == "stored" and archive.read(row["name"]) == b"v" * (COMPRESSED_SEGMENT_BYTES + 7) for row in report)
        assert packing_policy(True)["wholeStoredVideoAssets"] == sorted(WHOLE_STORED_VIDEO_ASSETS)
        startup_entries = []
        for name in WHOLE_STORED_STARTUP_ASSETS:
            startup = root / name
            startup.parent.mkdir(parents=True, exist_ok=True)
            startup.write_bytes(b"s" * (COMPRESSED_SEGMENT_BYTES + 7))
            entries = payload_entries([startup], root, compress=True)
            assert len(entries) == 1 and entries[0]["name"] == name and entries[0]["range_offset"] == 0
            startup_entries += entries
        with zipfile.ZipFile(root / "startup.zip", "w", allowZip64=False) as archive:
            report = add_payload(archive, startup_entries, compress=True)
        with zipfile.ZipFile(root / "startup.zip") as archive:
            assert all(info.compress_type == zipfile.ZIP_STORED for info in archive.infolist())
            assert all(row["compression"] == "stored" and archive.read(row["name"]) == b"s" * (COMPRESSED_SEGMENT_BYTES + 7) for row in report)
        assert packing_policy(True)["wholeStoredStartupAssets"] == list(WHOLE_STORED_STARTUP_ASSETS)
        for name in ("assets/bin/Data/sharedassets2.resource", "assets/bin/Data/sharedassets0.assets.resS.resource",
                     "assets/sharedassets0.resource", "lib/arm64-v8a/libtest.so", "classes.dex"):
            assert payload_compression(name, True) == zipfile.ZIP_DEFLATED, name
    print("APK packager self-test passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path)
    parser.add_argument("--unity-jar", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--android-jar", type=Path)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--compress", action="store_true", help="Deflate assets, DEX and native libraries inside the APK")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.app or not args.unity_jar or not args.output:
        parser.error("--app, --unity-jar and --output are required")
    config = json.loads(MACHINE_CONFIG.read_text(encoding="utf-8"))
    tools = Path(config["project_root"]) / "tools" / "android" / "sdk" / "build-tools" / config["build_tools_version"]
    java = Path(config["java_exe"])
    android_jar = args.android_jar or Path(os.environ["LOCALAPPDATA"]) / "Android" / "Sdk" / "platforms" / "android-32" / "android.jar"
    signer_dir = Path(config["signer"]["dir"])
    signer = {key: signer_dir / config["signer"][field]
              for key, field in (("jar", "apksigner"), ("key", "key"), ("cert", "cert"))}
    signer["expected"] = config["signer"]["expected_cert_sha256"].lower()
    for required in [java, tools / "aapt2.exe", tools / "zipalign.exe", tools / "lib" / "d8.jar",
                     android_jar, args.unity_jar, *[signer[key] for key in ("jar", "key", "cert")]]:
        if not required.is_file():
            raise FileNotFoundError(required)
    cert_text = signer["cert"].read_text(encoding="ascii")
    cert_der = base64.b64decode("".join(line for line in cert_text.splitlines() if not line.startswith("-----")))
    if hashlib.sha256(cert_der).hexdigest() != signer["expected"]:
        raise RuntimeError("Configured signing certificate does not match expected SHA-256")
    source = args.app.resolve(strict=True)
    output = args.output.resolve()
    if source == output.parent or source in output.parents:
        raise ValueError("Build output must be outside canonical app source")
    output.parent.mkdir(parents=True, exist_ok=True)
    base_identity = manifest_identity(source / "AndroidManifest.xml")
    if base_identity[2]:
        raise ValueError("Base manifest must not have a split name")
    dex_dir = output.parent / "_apkbuild_dex"
    dex_dir.mkdir(exist_ok=True)
    for old in dex_dir.glob("classes*.dex"):
        old.unlink()
    java_inputs = sorted((source / "src").rglob("*.java")) if (source / "src").is_dir() else []
    d8_inputs = [args.unity_jar]
    if java_inputs:
        classes_dir = dex_dir / "classes"
        classes_dir.mkdir(exist_ok=True)
        # Compile only current sources; stale .class files must not enter the APK.
        for old in classes_dir.rglob("*.class"):
            old.unlink()
        javac = java.with_name("javac.exe")
        run([javac, "--release", "8", "-classpath", os.pathsep.join(map(str, [android_jar, args.unity_jar])),
             "-d", classes_dir, *java_inputs], dex_dir / "javac.log")
        d8_inputs += sorted(classes_dir.rglob("*.class"))
    run([java, "-cp", tools / "lib" / "d8.jar", "com.android.tools.r8.D8", "--min-api", "23",
         "--lib", android_jar, "--output", dex_dir, *d8_inputs], dex_dir / "d8.log")
    dex_files = sorted(dex_dir.glob("classes*.dex"))
    if not dex_files:
        raise RuntimeError("D8 produced no DEX")
    results = [package_target(source, output, dex_files, tools, java, signer, android_jar, args.compress)]
    splits_root = source / "splits"
    for split in sorted(splits_root.iterdir()) if splits_root.is_dir() else []:
        if not split.is_dir():
            continue
        identity = manifest_identity(split / "AndroidManifest.xml")
        if identity[:2] != base_identity[:2] or not identity[2]:
            raise ValueError(f"Split package/version/name mismatch: {split}")
        if not re.fullmatch(r"[A-Za-z_]\w*", identity[2]):
            raise ValueError(f"Unsafe split name: {identity[2]}")
        results.append(package_target(split, output.with_name(identity[2] + ".apk"), [],
                                      tools, java, signer, android_jar, args.compress))
    metadata = output.with_suffix(".build.json")
    metadata.write_text(json.dumps({"artifacts": results, "packingPolicy": packing_policy(args.compress),
                                    "unity_jar_sha256": sha256(args.unity_jar)}, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"artifacts": results, "metadata": str(metadata)}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, zipfile.LargeZipFile, ET.ParseError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
