"""Prepare guarded Russian I2 raw objects in work; never modify source containers."""
from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path

import UnityPy

from i2_serialized import Reader, Writer, add_russian, mono_header_end, parse, require, serialize

ROOT = Path(__file__).resolve().parents[2]


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def replace_primary_term(raw, old, new):
    start = mono_header_end(raw)
    reader = Reader(raw, start)
    require(reader.string() == old, "Localize primary term differs from expected preimage")
    end = reader.p
    writer = Writer(raw[:start])
    writer.string(new)
    result = bytes(writer.raw) + raw[end:]
    check = Reader(result, start)
    require(check.string() == new and result[check.p:] == raw[end:], "Localize patch changed fields beyond primary term")
    return result


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--source", type=Path, default=ROOT / "app/obb/assets/bin/Data")
    cli.add_argument("--original-data", type=Path, default=ROOT / "external/game/TheClosingShift_Data")
    cli.add_argument("--out", type=Path, default=ROOT / "work/russian_localization/candidate")
    args = cli.parse_args()
    source, output = args.source.resolve(strict=True), args.out.resolve()
    require(output.is_relative_to(ROOT / "work"), "Russian candidates must stay inside work")
    inputs = Path(__file__).with_name("data")
    translation_path, supplement_path = inputs / "translations_ru.json", inputs / "supplemental_terms.json"
    translated = json.loads(translation_path.read_text(encoding="utf-8"))
    supplemental = json.loads(supplement_path.read_text(encoding="utf-8"))
    original_environment = UnityPy.load(str(args.original_data.resolve(strict=True) / "resources.assets"))
    try:
        original_raw = original_environment.file.objects[13893].get_raw_data()
    finally:
        original_environment.file.reader.dispose()
    require(sha(original_raw) == supplemental["baseSourceSha256"], "Translation source identity differs")
    original = parse(original_raw)
    require(serialize(original) == original_raw, "Original I2 byte-exact roundtrip failed")
    output.mkdir(parents=True, exist_ok=True)
    grouped = defaultdict(list)
    for row in supplemental["componentTermOverrides"]:
        grouped[row["file"]].append(row)
    files = []
    for filename in sorted({"resources.assets", *grouped}):
        path = source / filename
        require(path.resolve(strict=True).is_relative_to(source) and Path(filename).name == filename, "Metadata source escaped staging")
        raw_file = path.read_bytes()
        environment = UnityPy.load(raw_file)
        objects = {obj.path_id: obj for obj in environment.objects}
        changes = []
        if filename == "resources.assets":
            obj = objects[13893]
            require(obj.type.name == "MonoBehaviour", "I2 source object class differs")
            raw = obj.get_raw_data()
            current = parse(raw)
            require(serialize(current) == raw and current["frequency"] == 1, "Canonical I2 must roundtrip and remain offline")
            # Canonical localization differs from immutable source only by Weekly -> Never.
            expected = bytearray(original_raw)
            expected[original["frequencyOffset"]:original["frequencyOffset"] + 4] = (1).to_bytes(4, "little")
            require(raw == bytes(expected), "Canonical I2 has changes beyond the existing offline override")
            changed, result = add_russian(current, translated, supplemental)
            require("Перевод на русский: ChatGPT (OpenAI)" in next(t for t in result["terms"] if t["term"] == "Credits_ThankYou")["languages"][4], "Visible credits translation lacks AI attribution")
            changes.append((13893, raw, changed, {"kind": "I2LanguageSource", "language_columns": 5,
                "original_terms": len(current["terms"]), "total_terms": len(result["terms"]), "google_frequency": "Never=1",
                "network_tail_sha256": sha(current["networkTail"]), "original_four_columns_preserved": True}))
        for row in grouped[filename]:
            obj = objects[row["localizePathId"]]
            require(obj.type.name == "MonoBehaviour", "Localize component class differs")
            raw = obj.get_raw_data()
            changed = replace_primary_term(raw, row["originalTerm"], row["replacementTerm"])
            changes.append((obj.path_id, raw, changed, {"kind": "LocalizePrimaryTerm", **row}))
        records = []
        for key, raw, changed, detail in changes:
            name = f"{filename}_{key}.bin"
            (output / name).write_bytes(changed)
            records.append({"path_id": key, "old_sha256": sha(raw), "new_sha256": sha(changed),
                            "object_file": name, "before_size": len(raw), "after_size": len(changed), "detail": detail})
        files.append({"file": filename, "source_file_sha256": sha(raw_file), "objects": records})
        environment.file.reader.dispose()
    manifest = {"format": "closing-shift-russian-i2-v1", "source_original_object_sha256": sha(original_raw),
                "translations_sha256": sha(translation_path.read_bytes()), "supplemental_sha256": sha(supplement_path.read_bytes()),
                "language": supplemental["language"], "source_files_unchanged": True, "files": files}
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"manifest": str(output / "manifest.json"), "files": len(files), "raw_objects": sum(len(f["objects"]) for f in files),
                      "original_i2_roundtrip": "byte-exact", "canonical_offline_override": "preserved"}, indent=2))


if __name__ == "__main__":
    main()
