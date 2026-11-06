"""Prepare guarded recipe headings and landscape-only PlayerSettings from exact inputs."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

import UnityPy

sys.path.insert(0, str(Path(__file__).parent / 'russian_localization'))
from i2_serialized import parse, serialize, require

ROOT = Path(__file__).resolve().parents[1]
INPUTS = {
    'level1': '70a02721f84bfd40ee34c1e1cfdf2aa87b8a0c9d784e375d0fc0af2f83da67a4',
    'resources.assets': '7397ba75c4545dd4f733968b9cfc00a7511ba8f7367ce2eae265cc8853000b5d',
    'globalgamemanagers': '0f0b2fd9aeab7686c44a2b8433688bb213a4ef0ed72790f3591045c10ed97a21',
}

def sha(raw):
    return hashlib.sha256(raw).hexdigest()

def current(obj):
    return obj.data if obj.data is not None else obj.get_raw_data()

def prepare(name, raw):
    require(sha(raw) == INPUTS[name], 'Unreviewed UI preimage: ' + name)
    env = UnityPy.load(raw)
    objects = env.file.objects
    before = {pid: obj.get_raw_data() for pid, obj in objects.items()}
    if name == 'level1':
        obj = objects[35704]
        require(obj.type.name == 'RectTransform', 'Cold heading must have a RectTransform')
        tree = obj.read_typetree()
        require(tree['m_GameObject']['m_PathID'] == 1361 and tree['m_SizeDelta'] == {'x': 20.0, 'y': 5.0}, 'Cold heading owner or dimensions changed')
        tree['m_SizeDelta']['x'] = objects[35697].read_typetree()['m_SizeDelta']['x']
        obj.save_typetree(tree)
        changed = {35704}
    elif name == 'globalgamemanagers':
        obj = objects[1]
        require(obj.type.name == 'PlayerSettings', 'PlayerSettings identity changed')
        tree = obj.read_typetree()
        require(all(tree[key] for key in ('allowedAutorotateToPortrait', 'allowedAutorotateToPortraitUpsideDown', 'allowedAutorotateToLandscapeRight', 'allowedAutorotateToLandscapeLeft')), 'Orientation preimage changed')
        tree['allowedAutorotateToPortrait'] = tree['allowedAutorotateToPortraitUpsideDown'] = False
        obj.save_typetree(tree)
        changed = {1}
    else:
        obj = objects[13893]
        model = parse(before[13893])
        require(serialize(model) == before[13893], 'I2 input must round-trip exactly')
        index = next(i for i, language in enumerate(model['languages']) if language['code'] == 'ru')
        require(index == 4, 'Russian language column changed')
        replacements = {'DrinkRecipe_Cold_Label': ('Холодные', 'Холодные напитки'), 'DrinkRecipe_Hot_Label': ('Горячие', 'Горячие напитки')}
        found = set()
        for term in model['terms']:
            if term['term'] in replacements:
                old, new = replacements[term['term']]
                require(term['languages'][index] == old, 'Recipe translation preimage changed')
                term['languages'][index] = new
                found.add(term['term'])
        require(found == replacements.keys(), 'Both recipe headings are required')
        output = serialize(model)
        reverse = parse(output)
        for term in reverse['terms']:
            if term['term'] in replacements:
                term['languages'][index] = replacements[term['term']][0]
        require(serialize(reverse) == before[13893], 'Only the two Russian headings may change')
        obj.set_raw_data(output)
        changed = {13893}
    if name != 'resources.assets':
        result = bytearray(raw)
        for pid in changed:
            obj = objects[pid]
            value = current(obj)
            require(len(value) == len(before[pid]) and raw[obj.byte_start:obj.byte_start + len(value)] == before[pid], 'Fixed-size UI object span changed')
            result[obj.byte_start:obj.byte_start + len(value)] = value
        result = bytes(result)
    else:
        result = env.file.save()
    check = UnityPy.load(result)
    require(check.file.objects.keys() == objects.keys(), 'UI patch changed object identities')
    for pid, obj in check.file.objects.items():
        require(obj.get_raw_data() == current(objects[pid]), 'UI serialization changed an object: ' + str(pid))
        if pid not in changed:
            require(obj.get_raw_data() == before[pid], 'Unrelated UI object changed: ' + str(pid))
    require({pid for pid in objects if current(objects[pid]) != before[pid]} == changed, 'UI change scope differs')
    return result, dict(file=name, beforeSHA256=sha(raw), afterSHA256=sha(result), changedObjects=sorted(changed), unrelatedObjectsChecked=len(objects)-len(changed))

def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--scene', type=Path, required=True)
    cli.add_argument('--locale', type=Path, required=True)
    cli.add_argument('--settings', type=Path, required=True)
    cli.add_argument('--output', type=Path, required=True)
    args = cli.parse_args()
    output = args.output.resolve()
    require(output.is_relative_to(ROOT / 'work') and not output.exists(), 'Choose a new work-only output')
    candidates = []
    for name, path in (('level1', args.scene), ('resources.assets', args.locale), ('globalgamemanagers', args.settings)):
        raw = path.read_bytes()
        value, receipt = prepare(name, raw)
        try:
            prepare(name, raw + b'\0')
        except ValueError:
            pass
        else:
            raise AssertionError('UI preimage guard accepted changed input')
        candidates.append((name, value, receipt))
    output.mkdir(parents=True)
    for name, value, _ in candidates:
        (output / name).write_bytes(value)
    receipt = dict(status='MOBILE_UI_ASSETS_PASS', files=[row for _, _, row in candidates], negativeGuards=3, canonicalWrites=False)
    (output / 'manifest.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(receipt, ensure_ascii=False, indent=2))

if __name__ == '__main__':
    if not __debug__:
        raise RuntimeError('UI asset self-checks require Python without -O')
    main()
