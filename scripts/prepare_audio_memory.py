"""Audit packaged audio users and export guarded load-type-only work candidates."""
from __future__ import annotations

from collections import Counter
import copy
import hashlib
import json
from pathlib import Path
import struct

import UnityPy

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r"external/game/TheClosingShift_Data")
CANONICAL = ROOT / "app/obb/assets/bin/Data"
OUT = ROOT / "work/audio_memory_candidate"
EVIDENCE = ROOT / "work/evidence/audio_audit"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require(value, message):
    if not value:
        raise ValueError(message)


def dump(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(content, indent=2))


def resolve(file, pptr, externals):
    if not pptr["m_FileID"]:
        return file, pptr["m_PathID"]
    fid = pptr["m_FileID"]
    if not 1 <= fid <= len(externals):
        return None
    path = externals[fid - 1].replace("\\", "/")
    target = path[path.index("Resources/"):] if "Resources/" in path else path.rsplit("/", 1)[-1]
    return target, pptr["m_PathID"]


def walk(value, path=""):
    if isinstance(value, dict):
        if set(value) == {"m_FileID", "m_PathID"}:
            yield path, value
        else:
            for key, item in value.items():
                yield from walk(item, path + "/" + key)
    elif isinstance(value, list):
        for i, item in enumerate(value):
            yield from walk(item, path + "/" + str(i))


def main():
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)
    inventory = json.loads((ROOT / "work/evidence/asset_inventory/inventory.json").read_text())
    files = [entry["name"] for entry in inventory["files"]]
    scripts, clips = {}, {}
    for filename in files:
        env = UnityPy.load(str(SOURCE / filename))
        for obj in env.objects:
            if obj.type.name == "MonoScript":
                tree = obj.read_typetree()
                scripts[(filename, obj.path_id)] = {"class": (tree["m_Namespace"] + "." if tree["m_Namespace"] else "") + tree["m_ClassName"], "assembly": tree["m_AssemblyName"]}
            elif obj.type.name == "AudioClip":
                tree = obj.read_typetree()
                resource = tree["m_Resource"]
                stream_path = SOURCE / resource["m_Source"]
                with stream_path.open("rb") as stream:
                    stream.seek(resource["m_Offset"])
                    payload = stream.read(resource["m_Size"])
                require(len(payload) == resource["m_Size"] and payload[:4] == b"FSB5", "Unexpected packaged audio container")
                clips[(filename, obj.path_id)] = {"file": filename, "path_id": obj.path_id, "tree": tree, "original_object_sha256": sha(obj.get_raw_data()), "payload_sha256": sha(payload),
                                               "pcm16_estimate_bytes": round(tree["m_Length"] * tree["m_Frequency"]) * tree["m_Channels"] * 2,
                                               "pcm32_estimate_bytes": round(tree["m_Length"] * tree["m_Frequency"]) * tree["m_Channels"] * 4,
                                               "users": []}
        env.file.reader.dispose()
    autosave_roots, mono_counts = [], Counter()
    object_counts = {}
    for filename in files:
        env = UnityPy.load(str(SOURCE / filename))
        externals = [e.path for e in env.file.externals]
        count = 0
        for obj in env.objects:
            if obj.type.name not in {"MonoBehaviour", "AudioSource", "AnimationClip", "ResourceManager"}:
                continue
            count += 1
            tree = obj.read_typetree(check_read=obj.type.name != "MonoBehaviour")
            owner = {"file": filename, "path_id": obj.path_id, "type": obj.type.name}
            if obj.type.name == "MonoBehaviour":
                script = scripts.get(resolve(filename, tree["m_Script"], externals), {"class": "<missing>", "assembly": ""})
                owner.update(script)
                mono_counts[script["class"]] += 1
                if script["class"] in {"ES3AutoSave", "ES3AutoSaveMgr"}:
                    autosave_roots.append(owner)
                # Player MonoBehaviour trees omit user fields. Read aligned PPtr
                # candidates conservatively; they are evidence, not typed claims.
                raw = obj.get_raw_data()
                found = set()
                for offset in range(0, len(raw) - 11, 4):
                    fid, pid = struct.unpack_from("<iq", raw, offset)
                    if not 0 <= fid <= len(externals) or pid <= 0:
                        continue
                    key = resolve(filename, {"m_FileID": fid, "m_PathID": pid}, externals)
                    if key in clips and key not in found:
                        clips[key]["users"].append({**owner, "raw_pptr_offset": offset, "confidence": "aligned serialized PPtr candidate"})
                        found.add(key)
            else:
                for field, pptr in walk(tree):
                    key = resolve(filename, pptr, externals)
                    if key in clips:
                        clips[key]["users"].append({**owner, "field": field, "confidence": "typed PPtr"})
        object_counts[filename] = count
        env.file.reader.dispose()
    managed = json.loads((EVIDENCE / "managed_calls.json").read_text())
    sample_calls = [call for call in managed["direct_sample_calls"] if not call["assembly"].startswith("UnityEngine.")]
    require({c["type"] for c in sample_calls} == {"ES3Types.ES3Type_AudioClip"}, "Unreviewed packaged audio CPU sample consumer")
    dump(EVIDENCE / "autosave_roots.json", autosave_roots)
    require(not any(r["class"] == "ES3AutoSave" for r in autosave_roots), "Serialized autosave root requires per-clip CPU-read exclusions")
    external_autosave_calls = [c for c in managed["autosave_calls"] if not c["type"].startswith(("ES3", "ES3Types.", "ES3Internal."))]
    require(not external_autosave_calls, "External autosave construction/call requires review")
    require(not managed["external_serializer_calls"], "External manual ES3 writer/type/file use requires review")
    game_saves = [c for c in managed["save_calls"] if c["assembly"] == "Assembly-CSharp.dll"]
    allowed = ["<System.Int32>", "<System.Boolean>", "<System.String>", "<System.Single>", "<UnityEngine.Vector3>", "<UnityEngine.Quaternion>",
               "<System.Collections.Generic.List`1<System.Boolean>>", "<System.Collections.Generic.List`1<System.String>>", "<System.Collections.Generic.List`1<UnityEngine.Transform>>"]
    require(all(any(t in c["target"] for t in allowed) for c in game_saves), "Unreviewed game save payload may include AudioClip PCM")
    audit = {"assemblies_scanned": len(managed["assemblies"]), "managed_sample_calls": sample_calls, "serialized_autosave_roots": autosave_roots,
             "game_save_targets": dict(Counter(c["target"] for c in game_saves)), "game_save_call_count": len(game_saves),
             "object_counts_scanned": object_counts, "mono_class_counts": dict(mono_counts), "clips": list(clips.values())}
    dump(EVIDENCE / "asset_users.json", audit)
    prepared = []
    for filename in sorted({key[0] for key in clips}):
        path = CANONICAL / filename
        env = UnityPy.load(path.read_bytes())
        before = {obj.path_id: sha(obj.get_raw_data()) for obj in env.objects}
        changed, expected = [], {}
        for obj in env.objects:
            key = (filename, obj.path_id)
            if key not in clips:
                continue
            tree = obj.read_typetree()
            source = clips[key]["tree"]
            # Existing Android stream aliases are allowed; every other audio
            # field must still match the original shipping object exactly.
            compare = copy.deepcopy(tree)
            compare["m_Resource"]["m_Source"] = source["m_Resource"]["m_Source"]
            require(compare == source, "Canonical audio metadata unexpectedly changed")
            if tree["m_CompressionFormat"] != 1 or tree["m_LoadType"] != 0:
                continue
            original = obj.get_raw_data()
            tree["m_LoadType"] = 1
            raw = obj.save_typetree(tree)
            delta = [i for i, (a, b) in enumerate(zip(original, raw)) if a != b]
            require(len(original) == len(raw) and len(delta) == 1 and original[delta[0]] == 0 and raw[delta[0]] == 1, "Audio edit is not one 0->1 byte")
            relative = Path("objects") / f"{Path(filename).stem}_{obj.path_id}.bin"
            (OUT / relative).parent.mkdir(exist_ok=True)
            (OUT / relative).write_bytes(raw)
            expected[obj.path_id] = tree
            info = clips[key]
            changed.append({"path_id": obj.path_id, "name": tree["m_Name"], "object_file": relative.as_posix(), "old_sha256": before[obj.path_id], "new_sha256": sha(raw),
                            "changed_byte_offset": delta[0], "old_load_type": 0, "new_load_type": 1, "compression_format": 1,
                            "payload_sha256": info["payload_sha256"], "payload_bytes": source["m_Resource"]["m_Size"],
                            "bits_per_sample": tree["m_BitsPerSample"], "length": tree["m_Length"], "frequency": tree["m_Frequency"], "channels": tree["m_Channels"],
                            "pcm16_estimate_bytes": info["pcm16_estimate_bytes"], "pcm32_estimate_bytes": info["pcm32_estimate_bytes"]})
        saved = UnityPy.load(env.file.save())
        after = {obj.path_id: obj for obj in saved.objects}
        require(after.keys() == before.keys(), "Audio serialization changed path IDs")
        for key, obj in after.items():
            if key in expected:
                require(obj.read_typetree() == expected[key], "Audio semantic roundtrip differs")
            else:
                require(sha(obj.get_raw_data()) == before[key], "Unrelated object changed")
        prepared.append({"file": filename, "canonical_snapshot_sha256": file_hash(path), "clips": changed, "unchanged_raw_objects_verified": len(before) - len(changed)})
        env.file.reader.dispose(); saved.file.reader.dispose()
    all_clips = [c for f in prepared for c in f["clips"]]
    summary = {"changed_clips": len(all_clips), "compressed_payload_bytes_unchanged": sum(c["payload_bytes"] for c in all_clips),
               "source_bits_per_sample": dict(Counter(c["bits_per_sample"] for c in all_clips)),
               "pcm16_estimate_bytes": sum(c["pcm16_estimate_bytes"] for c in all_clips), "pcm32_estimate_bytes": sum(c["pcm32_estimate_bytes"] for c in all_clips),
               "actual_native_allocation_measured": False, "native_playback_verified": False, "audio_payload_bytes_changed": 0}
    dump(OUT / "manifest.json", {"cpu_sample_use_proof": "Packaged PCM serializer exists only in unused ES3 audio serializer; no ES3AutoSave objects or external autosave calls; sole level0 ES3AutoSaveMgr collects an empty set; game saves only reviewed scalar/value/Transform payloads", "audit_sha256": file_hash(EVIDENCE / "asset_users.json"), "managed_audit_sha256": file_hash(EVIDENCE / "managed_calls.json"), "files": prepared, "summary": summary})
    print("AUDIO_MEMORY_CANDIDATE_VERIFIED", json.dumps(summary))


if __name__ == "__main__":
    main()
