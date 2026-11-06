"""Reproduce the one-cell Russian reset-button label; default writes only work.

Only resources.assets:13893 / UI_Default / Russian changes. The four original
columns, all other Russian cells, AI flags, offline tail and unrelated objects
remain exact. --apply is explicit; canonical apply also needs --allow-canonical.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
import sys

import UnityPy

from i2_serialized import parse, require, serialize

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from prepare_depth_precision import container_layout

CANONICAL = ROOT / "app/obb/assets/bin/Data/resources.assets"
OLD_SHA = "5a55de543246775459f72746299fcd2cd818fce7099b6c69856daa8466d2c2c1"
NEW_SHA = "ce78677221455f38b845c59bca1c9b4850266340d06af03faf221c768c687b4c"
TERM, OLD_LABEL, NEW_LABEL = "UI_Default", "По умолчанию", "Сбросить"


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def replace_cell(raw, old, new):
    model = parse(raw)
    require(serialize(model) == raw, "I2 byte roundtrip differs")
    require(len(model["languages"]) == 5 and model["languages"][4] ==
            {"name": "Русский (AI)", "code": "ru", "flags": 0}, "I2 Russian identity differs")
    require(model["frequency"] == 1, "I2 must stay offline Never=1")
    rows = [row for row in model["terms"] if row["term"] == TERM]
    require(len(rows) == 1 and len(rows[0]["languages"]) == 5 and
            rows[0]["languages"][4] == old, "Reset-label preimage differs")
    changed = copy.deepcopy(model)
    row = next(row for row in changed["terms"] if row["term"] == TERM)
    row["languages"][4] = new
    result = serialize(changed)
    check = parse(result)
    require(serialize(check) == result and check["networkTail"] == model["networkTail"], "I2 tail/roundtrip differs")
    restored = copy.deepcopy(check)
    next(row for row in restored["terms"] if row["term"] == TERM)["languages"][4] = old
    require(serialize(restored) == raw, "Change exceeds one Russian cell")
    return result


def repair(raw):
    identity = sha(raw)
    require(identity in (OLD_SHA, NEW_SHA), "Unreviewed I2 raw preimage")
    original = raw if identity == OLD_SHA else replace_cell(raw, NEW_LABEL, OLD_LABEL)
    require(sha(original) == OLD_SHA, "Reverse preimage differs")
    result = replace_cell(original, OLD_LABEL, NEW_LABEL)
    require(sha(result) == NEW_SHA and replace_cell(result, NEW_LABEL, OLD_LABEL) == original,
            "Reset-label output/reversal differs")
    return result, identity == OLD_SHA


def checked_target(path, mutate, allow_canonical):
    require(not path.is_symlink(), "Symlink target")
    target = path.resolve(strict=True)
    canonical = CANONICAL.resolve(strict=True)
    require(canonical.is_relative_to((ROOT / "app").resolve()), "Canonical escaped app")
    require(target.is_file() and target.name == "resources.assets" and
            (target == canonical or target.is_relative_to((ROOT / "work").resolve())), "Target escaped allowed roots")
    require(not mutate or target != canonical or allow_canonical, "Canonical apply requires --allow-canonical")
    return target


def plan(target):
    source = target.read_bytes()
    env = UnityPy.load(source)
    try:
        layout = container_layout(env.file)
        before = {i: sha(o.get_raw_data()) for i, o in env.file.objects.items()}
        obj = env.file.objects[13893]
        require(obj.type.name == "MonoBehaviour", "I2 object class differs")
        raw = obj.get_raw_data()
        new, changed = repair(raw)
        if changed:
            obj.set_raw_data(new)
        result = env.file.save() if changed else source
        check = UnityPy.load(result)
        try:
            require(container_layout(check.file) == layout and check.file.objects.keys() == before.keys(), "Container layout/IDs differ")
            require(all(sha(o.get_raw_data()) == (NEW_SHA if i == 13893 else before[i])
                        for i, o in check.file.objects.items()), "I2 or unrelated raw object differs")
            require(check.file.objects[13893].get_raw_data() == new, "Saved raw payload differs")
        finally:
            check.file.reader.dispose()
    finally:
        env.file.reader.dispose()
    require(sha(target.read_bytes()) == sha(source), "Input changed during validation")
    detail = dict(kind="I2LanguageSource", term=TERM, language_columns=5, old_label=OLD_LABEL,
                  new_label=NEW_LABEL, original_four_columns_preserved=True,
                  all_other_ru_and_fields_byte_exact_after_reversing_one_cell=True,
                  google_frequency="Never=1", network_tail_sha256=sha(parse(new)["networkTail"]))
    row = dict(path_id=13893, old_sha256=OLD_SHA, new_sha256=NEW_SHA,
               object_file="resources.assets_13893.bin", detail=detail)
    receipt = dict(format="closing-shift-russian-i2-v1", source_files_unchanged=True,
                   files=[dict(file="resources.assets", source_file_sha256=sha(source), objects=[row])],
                   source=str(target), new_container_sha256=sha(result), new_container_bytes=len(result),
                   changed_objects=int(changed), unrelated_raw_objects_preserved=len(before) - 1,
                   container_layout_preserved=True, canonical_or_device_modified=False)
    return receipt, result, new


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--target", type=Path, default=CANONICAL)
    cli.add_argument("--output", type=Path, default=ROOT / "work/russian_localization/candidate-resetlabel")
    cli.add_argument("--allow-canonical", action="store_true")
    mode = cli.add_mutually_exclusive_group()
    mode.add_argument("--apply", action="store_true")
    mode.add_argument("--verify-only", action="store_true")
    mode.add_argument("--self-check", action="store_true")
    args = cli.parse_args()
    target = checked_target(args.target, args.apply, args.allow_canonical)
    receipt, result, raw = plan(target)
    if args.self_check:
        require(repair(raw) == (raw, False), "Idempotence failed")
        try:
            repair(bytes([raw[0] ^ 1]) + raw[1:])
        except ValueError:
            pass
        else:
            raise ValueError("Corrupt I2 accepted")
    if args.apply and receipt["changed_objects"]:
        temporary = target.with_name(target.name + ".reset-label.tmp")
        require(not temporary.exists() and not temporary.is_symlink(), "Stale reset-label temporary")
        with temporary.open("xb") as stream:
            stream.write(result)
        require(sha(temporary.read_bytes()) == receipt["new_container_sha256"], "Temporary payload differs")
        require(sha(target.read_bytes()) == receipt["files"][0]["source_file_sha256"], "Concurrent input change")
        temporary.replace(target)
        receipt["canonical_or_device_modified"] = target == CANONICAL.resolve()
    elif not (args.apply or args.verify_only or args.self_check):
        output = args.output.resolve()
        require(output.is_relative_to((ROOT / "work").resolve()) and not output.exists(), "Output must be fresh work directory")
        require(not target.is_relative_to(output), "Output overlaps source")
        output.mkdir(parents=True)
        (output / "resources.assets").write_bytes(result)
        (output / "resources.assets_13893.bin").write_bytes(raw)
        require(sha((output / "resources.assets").read_bytes()) == receipt["new_container_sha256"], "Output write differs")
        (output / "manifest.json").write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    receipt["status"] = "RESET_LABEL_APPLY_PASS" if args.apply else "RESET_LABEL_PREFLIGHT_PASS"
    print(json.dumps(receipt, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
