"""Build classic ZIP Unity main/patch caches for direct native reads, without extraction."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import tempfile
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
ANDROID = "{http://schemas.android.com/apk/res/android}"
ZIP_BUDGET = 4_000_000_000
STREAM_SEGMENT_BYTES = 512 * 1024 * 1024
ENTRY_LIMIT = (1 << 31) - 1
zipfile.ZIP64_LIMIT = (1 << 32) - 1


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def virtual_entries(files, source, segment_bytes=STREAM_SEGMENT_BYTES):
    entries = []
    for path in files:
        size = path.stat().st_size
        name = path.relative_to(source).as_posix()
        if path.name.endswith(".resS"):
            name += ".resource"
        segmented = path.name.endswith(".resS") and size > segment_bytes
        if not segmented and size > ENTRY_LIMIT:
            raise ValueError(f"Native ZIP entry exceeds 2 GiB: {name}")
        for index, offset in enumerate(range(0, size, segment_bytes) if segmented else (0,)):
            entries.append({"source_path": path, "name": name + f".split{index}" if segmented else name, "range_offset": offset, "bytes": min(segment_bytes, size - offset) if segmented else size})
    assert len({entry["name"] for entry in entries}) == len(entries), "Duplicate virtual asset names"
    return entries


def partition(entries, budget):
    groups = {}
    for entry in entries:
        path = entry["source_path"]
        # External streams resolve inside the SerializedFile's originating archive.
        stem = path.name.removesuffix(".resS").removesuffix(".resource").removesuffix(".assets")
        groups.setdefault((path.parent, stem), []).append(entry)
    bins = [[], []]
    sizes = [4096, 4096]
    grouped = [(group, sum(entry["bytes"] + len(entry["name"].encode()) * 2 + 256 for entry in group)) for group in groups.values()]
    for group, size in sorted(grouped, key=lambda item: (-item[1], item[0][0]["name"])):
        index = next((i for i in range(2) if sizes[i] + size < budget), None)
        if index is None:
            raise ValueError("Related metadata/streams exceed two classic ZIP caches; keep each complete group in one archive")
        bins[index].extend(group)
        sizes[index] += size
    return bins


def build_archives(source, output, package, version, build_id, budget=ZIP_BUDGET, segment_bytes=STREAM_SEGMENT_BYTES):
    assert output.resolve().is_relative_to((ROOT / "work").resolve()), "Generated cache output must stay inside work"
    marker = source / "assets" / build_id
    assert marker.is_file() and marker.stat().st_size == 0, "Matching Unity OBB build marker required"
    files = [p for p in source.rglob("*") if p.is_file() and p != marker]
    assert files and all(p.resolve().is_relative_to(source.resolve()) and not p.is_symlink() for p in files)
    originals = {str(path): {"bytes": path.stat().st_size, "sha256": digest(path)} for path in files}
    all_entries = virtual_entries(files, source, segment_bytes)
    bins = partition(all_entries, budget)
    output.mkdir(parents=True, exist_ok=True)
    results = []
    for label, entries in zip(("main", "patch"), bins):
        if not entries:
            continue
        destination = output / f"{label}.{version}.{package}.obb"
        temporary = destination.with_suffix(".obb.tmp")
        contents = []
        with zipfile.ZipFile(temporary, "w", allowZip64=False) as archive:
            archive.write(marker, marker.relative_to(source).as_posix(), compress_type=zipfile.ZIP_STORED)
            for entry in sorted(entries, key=lambda entry: (".resS" in entry["name"], entry["name"])):
                path = entry["source_path"]
                metadata = zipfile.ZipInfo.from_file(path, entry["name"])
                metadata.file_size = entry["bytes"]
                metadata.compress_type = zipfile.ZIP_STORED
                part_hash = hashlib.sha256()
                remaining = entry["bytes"]
                with path.open("rb") as stream, archive.open(metadata, "w", force_zip64=False) as target:
                    stream.seek(entry["range_offset"])
                    while remaining:
                        data = stream.read(min(remaining, 1024 * 1024))
                        assert data, f"Truncated source range: {path}"
                        target.write(data)
                        part_hash.update(data)
                        remaining -= len(data)
                contents.append({"name": entry["name"], "source_path": str(path), "range_offset": entry["range_offset"], "bytes": entry["bytes"], "sha256": part_hash.hexdigest()})
        assert temporary.stat().st_size < budget
        with zipfile.ZipFile(temporary) as archive:
            assert len(archive.namelist()) == len(set(archive.namelist()))
            assert all(entry.extract_version < 45 for entry in archive.infolist()), "ZIP64 is unsupported by the exact runtime"
            assert all(entry.file_size <= ENTRY_LIMIT and entry.compress_type == zipfile.ZIP_STORED for entry in archive.infolist())
        os.replace(temporary, destination)
        results.append({"file": str(destination), "bytes": destination.stat().st_size, "sha256": digest(destination), "entries": contents})
        print(label, destination.stat().st_size, "bytes", flush=True)
    assert sum(len(item["entries"]) for item in results) == len(all_entries)
    locations = {}
    for cache in results:
        for entry in cache["entries"]:
            locations.setdefault(entry["source_path"], []).append((cache["file"], entry))
    for path, original in originals.items():
        source_hash = hashlib.sha256()
        cursor = 0
        for filename, entry in sorted(locations[path], key=lambda item: item[1]["range_offset"]):
            assert entry["range_offset"] == cursor, "Missing/overlapping source ranges"
            part_hash, count = hashlib.sha256(), 0
            with zipfile.ZipFile(filename) as archive, archive.open(entry["name"]) as stream:
                while data := stream.read(1024 * 1024):
                    part_hash.update(data)
                    source_hash.update(data)
                    count += len(data)
            assert count == entry["bytes"] and part_hash.hexdigest() == entry["sha256"], entry["name"]
            cursor += count
        assert cursor == original["bytes"] and source_hash.hexdigest() == original["sha256"], f"Concatenation differs: {path}"
        assert Path(path).stat().st_size == original["bytes"] and digest(Path(path)) == original["sha256"], f"Source changed during build: {path}"
    active = {Path(cache["file"]).name for cache in results}
    for label in ("main", "patch"):
        obsolete = output / f"{label}.{version}.{package}.obb"
        if obsolete.name not in active and obsolete.exists():
            assert obsolete.resolve().is_relative_to(output.resolve()) and obsolete.is_file()
            obsolete.unlink()
    result = {"build_id": build_id, "package": package, "version_code": version, "segment_bytes": segment_bytes, "sources": originals, "caches": results}
    (output / "obb-manifest.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print("PASS: main/patch OBB, matching markers, classic ZIP, every range and concatenated source SHA256 verified")
    return result


def selftest():
    work = ROOT / "work"
    work.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="obb_selftest_", dir=work) as temporary:
        root = Path(temporary).resolve()
        assert root.is_relative_to(work.resolve())
        source, output = root / "source", root / "output"
        data = source / "assets/bin/Data"
        data.mkdir(parents=True)
        (source / "assets/test-build-id").write_bytes(b"")
        payload = bytes(range(256)) * 20 + b"tail"
        (data / "large.resS").write_bytes(payload)
        (data / "exact.resS").write_bytes(b"e" * 1024)
        (data / "small.resS").write_bytes(b"s" * 1023)
        (data / "large").write_bytes(b"stream-owner")
        (data / "large.resource").write_bytes(b"audio-owner")
        (data / "globalgamemanagers").write_bytes(b"metadata-preserved")
        result = build_archives(source, output, "test.obb", "1", "test-build-id", 14_000, 1024)
        entries = [entry for cache in result["caches"] for entry in cache["entries"]]
        segments = sorted((entry for entry in entries if "large.resS.resource.split" in entry["name"]), key=lambda entry: entry["range_offset"])
        assert len(result["caches"]) == 2 and len(segments) == 6
        assert [entry["range_offset"] for entry in segments] == list(range(0, len(payload), 1024))
        assert segments[-1]["bytes"] == 4
        assert {"assets/bin/Data/exact.resS.resource", "assets/bin/Data/small.resS.resource"}.issubset({entry["name"] for entry in entries})
        assert (data / "large.resS").read_bytes() == payload
        assert not list(source.rglob("*.split*")), "Builder must not materialize/mutate source segments"
        owners = {entry["name"]: index for index, cache in enumerate(result["caches"]) for entry in cache["entries"]}
        related = [entry["name"] for entry in entries if entry["source_path"].startswith(str(data / "large"))]
        assert len({owners[name] for name in related}) == 1, "Metadata, audio and every texture chunk must share one archive"
        single = build_archives(source, output, "test.obb", "1", "test-build-id", 30_000, 1024)
        assert len(single["caches"]) == 1 and not (output / "patch.1.test.obb.obb").exists(), "Obsolete patch must not shadow the new main cache"
    print("OBB_RANGE_SELFTEST_PASSED")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--app", type=Path, default=ROOT / "app")
    parser.add_argument("--output", type=Path, default=ROOT / "work/build/obb-baseline")
    args = parser.parse_args()
    if args.self_test:
        selftest()
        return
    app = args.app.resolve(strict=True)
    output = args.output.resolve()
    if output == app or app in output.parents:
        parser.error("--output must be outside the app source")
    source = app / "obb"
    manifest = ET.parse(app / "AndroidManifest.xml").getroot()
    package, version = manifest.get("package"), manifest.get(ANDROID + "versionCode")
    application = manifest.find("application")
    build_id = next(node.get(ANDROID + "value") for node in application.findall("meta-data") if node.get(ANDROID + "name") == "unity.build-id")
    build_archives(source, output, package, version, build_id)


if __name__ == "__main__":
    main()
