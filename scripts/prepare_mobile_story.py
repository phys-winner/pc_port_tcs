"""Prepare guarded credits timing and an unambiguous Russian detective line in work."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
import struct
import sys

import UnityPy

sys.path.insert(0, str(Path(__file__).parent / "russian_localization"))
from i2_serialized import parse, require, serialize
from prepare_depth_precision import container_layout

ROOT = Path(__file__).resolve().parents[1]
DATA = Path("obb/assets/bin/Data")
OLD_I2 = "e78d6678254594da0a1475da639c937c980a30a96831db23cff4537b809cbcf2"
OLD_CREDITS = "eb9cb001e93052ca8dd08d4a1eff5e30ce5f736218d9b994a1d55dbe2962b425"
NEW_CREDITS = "8fe42990e14d3dc9f170da2ea10ef4c2f5237880894107a5df23751afad9f67e"
CLIP_SHA = "f406b180560ae8c2c2cac2880f675cdc174fd112623a8ef02d548a271494b6da"
TERM = "Dialogue_PrivateInvestigator2"
OLD_LINE = "Осмотрюсь вокруг. Может, что-нибудь найду."
NEW_LINE = "Я осмотрю окрестности. Может, что-нибудь найду."


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def detective(raw):
    require(sha(raw) == OLD_I2, "Unreviewed I2 preimage")
    source = parse(raw)
    require(serialize(source) == raw and len(source["languages"]) == 5
            and source["languages"][4]["code"] == "ru", "I2 layout or Russian column changed")
    result = copy.deepcopy(source)
    rows = [row for row in result["terms"] if row["term"] == TERM]
    require(len(rows) == 1 and rows[0]["languages"][4].splitlines()[-1] == OLD_LINE,
            "Detective Russian line preimage changed")
    rows[0]["languages"][4] = rows[0]["languages"][4].removesuffix(OLD_LINE) + NEW_LINE
    output = serialize(result)
    check = parse(output)
    row = next(row for row in check["terms"] if row["term"] == TERM)
    row["languages"][4] = row["languages"][4].removesuffix(NEW_LINE) + OLD_LINE
    require(serialize(check) == raw, "Change exceeded one Russian sentence")
    return output


def credits(raw):
    require(sha(raw) == OLD_CREDITS, "Unreviewed credits sequence preimage")
    require(len(raw) == 304 and struct.unpack_from("<7i", raw, 32) == (0, 0, 0, 0, 0, 2, 1)
            and struct.unpack_from("<f", raw, 60)[0] == 146.0, "Credits event layout changed")
    output = bytearray(raw)
    struct.pack_into("<f", output, 60, 108.0)
    require(sha(output) == NEW_CREDITS and output[:60] == raw[:60] and output[64:] == raw[64:],
            "Credits change exceeded its one timer")
    return bytes(output)


def container(path, pid, repair):
    raw = path.read_bytes()
    env = UnityPy.load(raw)
    try:
        layout = container_layout(env.file)
        before = {i: sha(o.get_raw_data()) for i, o in env.file.objects.items()}
        obj = env.file.objects[pid]
        require(obj.type.name == "MonoBehaviour", "Story object type changed")
        original = obj.get_raw_data()
        changed = repair(original)
        obj.set_raw_data(changed)
        output = env.file.save()
        check = UnityPy.load(output)
        try:
            require(container_layout(check.file) == layout and check.file.objects.keys() == before.keys(),
                    "Story container identities changed")
            require(all(sha(o.get_raw_data()) == (sha(changed) if i == pid else before[i])
                        for i, o in check.file.objects.items()), "Unrelated story object changed")
        finally:
            check.file.reader.dispose()
        try:
            repair(changed)
        except ValueError:
            pass
        else:
            raise ValueError("Reapplying the same story patch was accepted")
        require(path.read_bytes() == raw, "Story source changed concurrently")
        return output, dict(file=path.name, sourceSha256=sha(raw), outputSha256=sha(output),
                            pathId=pid, oldRawSha256=sha(original), newRawSha256=sha(changed),
                            unrelatedRawObjectsPreserved=len(before) - 1,
                            containerLayoutPreserved=True, doublePatchRejected=True)
    finally:
        env.file.reader.dispose()


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--app", type=Path, default=ROOT / "app")
    cli.add_argument("--output", type=Path, default=ROOT / "work/mobile52-20261105/story/candidate")
    cli.add_argument("--self-test", action="store_true")
    args = cli.parse_args()
    app = args.app.resolve(strict=True)
    require(app == ROOT / "app" or app.is_relative_to(ROOT / "work"), "Source must stay in this project")
    video = UnityPy.load(str(app / DATA / "sharedassets0.assets"))
    try:
        clip = video.file.objects[1241]
        require(sha(clip.get_raw_data()) == CLIP_SHA, "Credits video metadata changed")
        tree = clip.read_typetree()
        duration = tree["m_FrameCount"] / tree["m_FrameRate"]
        require(106 < duration < 107 and 1 < 108 - duration < 2, "Credits tail must remain under two seconds")
    finally:
        video.file.reader.dispose()
    outputs = [container(app / DATA / fn, pid, repair)
               for fn, pid, repair in (("level1", 37867, credits), ("resources.assets", 13893, detective))]
    report = dict(status="MOBILE_STORY_SELFTEST_PASS" if args.self_test else "MOBILE_STORY_CANDIDATE_PASS",
                  canonicalOrDeviceModified=False, creditsVideoSeconds=duration,
                  oldCreditsWaitSeconds=146, newCreditsWaitSeconds=108,
                  detectiveOldLine=OLD_LINE, detectiveNewLine=NEW_LINE,
                  note="Original detective line already meant he; explicit subject removes visual ambiguity.",
                  files=[row for _, row in outputs])
    if args.self_test:
        for _, _, repair in (("level1", 37867, credits), ("resources.assets", 13893, detective)):
            try:
                repair(b"corrupt preimage")
            except ValueError:
                pass
            else:
                raise ValueError("Unknown story preimage accepted")
        report["unknownPreimagesRejected"] = 2
    else:
        out = args.output.resolve()
        require(out.is_relative_to(ROOT / "work") and not out.exists() and not app.is_relative_to(out),
                "Output must be a new work directory outside source")
        out.mkdir(parents=True)
        for payload, row in outputs:
            (out / row["file"]).write_bytes(payload)
            require(sha((out / row["file"]).read_bytes()) == row["outputSha256"], "Story output write differs")
        (out / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
