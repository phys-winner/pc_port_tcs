"""Dry-plan or apply verified raw I2/Localize objects; preserve every unrelated object."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import tempfile

import UnityPy

from i2_serialized import parse, require

ROOT = Path(__file__).resolve().parents[2]


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def file_state(file):
    return (file.unity_version, int(file.target_platform), [(e.path, e.guid.hex(), e.type) for e in file.externals])


def apply(candidate, target_root, commit):
    candidate, target_root = candidate.resolve(strict=True), target_root.resolve(strict=True)
    require(candidate.is_relative_to(ROOT / "work"), "Candidate must stay inside work")
    require(target_root.is_relative_to(ROOT / "app") or target_root.is_relative_to(ROOT / "work"), "Target must stay inside app or work")
    manifest = json.loads((candidate / "manifest.json").read_text(encoding="utf-8"))
    require(manifest["format"] == "closing-shift-russian-i2-v1", "Unknown localization manifest")
    reports = []
    for file in manifest["files"]:
        require(Path(file["file"]).name == file["file"], "Metadata path escaped target")
        target = target_root / file["file"]
        before_file = target.read_bytes()
        environment = UnityPy.load(before_file)
        state = file_state(environment.file)
        objects = {obj.path_id: obj for obj in environment.objects}
        before = {key: sha(obj.get_raw_data()) for key, obj in objects.items()}
        changes = {}
        for row in file["objects"]:
            key = row["path_id"]
            require(objects[key].type.name == "MonoBehaviour", "Localization object type differs")
            require(before[key] in (row["old_sha256"], row["new_sha256"]), "Localization object preimage differs")
            path = (candidate / row["object_file"]).resolve(strict=True)
            require(path.is_relative_to(candidate), "Candidate object escaped directory")
            raw = path.read_bytes()
            require(sha(raw) == row["new_sha256"], "Localization candidate changed")
            if row["detail"]["kind"] == "I2LanguageSource":
                model = parse(raw)
                require(model["frequency"] == 1 and model["languages"][4] == {"name": "Русский (AI)", "code": "ru", "flags": 0}, "I2 language/offline settings differ")
            if before[key] == row["new_sha256"]:
                continue
            objects[key].set_raw_data(raw)
            changes[key] = row["new_sha256"]
        serialized = environment.file.save()
        check = UnityPy.load(serialized)
        saved = {obj.path_id: obj for obj in check.objects}
        require(saved.keys() == objects.keys() and file_state(check.file) == state, "Localization changed object IDs/platform/external aliases")
        require(all(sha(obj.get_raw_data()) == changes.get(key, before[key]) for key, obj in saved.items()), "Localization changed unrelated raw object or persisted incorrect candidate")
        report = {"file": file["file"], "changed_objects": len(changes), "already_target": len(file["objects"]) - len(changes),
                  "unrelated_raw_objects_verified": len(objects) - len(changes), "ids_platform_externals_preserved": True,
                  "before_sha256": sha(before_file), "after_sha256": sha(serialized), "commit": commit}
        environment.file.reader.dispose()
        check.file.reader.dispose()
        if commit:
            temporary = target.with_name(target.name + ".russian-staging")
            require(not temporary.exists(), "Prior localization temporary file requires inspection")
            temporary.write_bytes(serialized)
            require(sha(temporary.read_bytes()) == report["after_sha256"], "Localization temporary write failed")
            temporary.replace(target)
        reports.append(report)
    return reports


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--candidate", type=Path, default=ROOT / "work/russian_localization/candidate")
    cli.add_argument("--staging", type=Path, default=ROOT / "app/obb/assets/bin/Data")
    cli.add_argument("--apply", action="store_true")
    cli.add_argument("--self-test", action="store_true")
    args = cli.parse_args()
    if args.self_test:
        with tempfile.TemporaryDirectory(prefix="i2-staging-test-", dir=ROOT / "work/russian_localization") as directory:
            target = Path(directory)
            manifest = json.loads((args.candidate / "manifest.json").read_text(encoding="utf-8"))
            for file in manifest["files"]:
                (target / file["file"]).write_bytes((args.staging / file["file"]).read_bytes())
            before = {p.name: sha(p.read_bytes()) for p in target.iterdir()}
            dry = apply(args.candidate, target, False)
            require(before == {p.name: sha(p.read_bytes()) for p in target.iterdir()}, "Dry plan changed localization fixture")
            first = apply(args.candidate, target, True)
            second = apply(args.candidate, target, True)
            require(all(not row["changed_objects"] for row in second), "Localization application is not idempotent")
            result = {"dry": dry, "first": first, "idempotent": second}
            (ROOT / "work/russian_localization/staging-selftest.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print("RUSSIAN_I2_STAGING_SELFTEST_PASS")
    else:
        print(json.dumps(apply(args.candidate, args.staging, args.apply), indent=2))


if __name__ == "__main__":
    main()
