"""Lift only the baked cafe wall decal off its receiver; write a verified work candidate."""
from pathlib import Path
import argparse
import copy
import json
import struct

import UnityPy

from prepare_light_shadow_ab import digest, identity, require, sha
from prepare_mobile_scene_budget import object_path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'app/obb/assets/bin/Data/level1'
SOURCE_SHA = '0b33fb931d102859fcb5bb5971c3a6a5c0c964cfbef4dca251652552782217cb'
MESH_SHA = '369b2797ff79c8017c8c5cf69e6d8c625e26f2b931230bf93e96e5a17ecb7602'
LIFT = .1  # Local Y is scaled by .141081944: 14.1 mm along the wall normal.


def prepare(source, output):
    require(output.is_relative_to(ROOT / 'work'), 'Candidate must be inside project work')
    require(digest(source) == SOURCE_SHA, 'Cafe scene preimage changed')
    env = UnityPy.load(str(source))
    try:
        file = env.file
        before = {i: sha(o.get_raw_data()) for i, o in file.objects.items()}
        layout = identity(file)
        require(object_path(file, 1188) == 'Store props/decals/logo/PROC_PLANE', 'Wrong decal')
        require(file.objects[26641].read_typetree()['m_Mesh'] == {'m_FileID': 0, 'm_PathID': 67}, 'Decal mesh reference changed')
        require(before[67] == MESH_SHA, 'Baked logo mesh changed')
        obj = file.objects[67]
        original = obj.read_typetree()
        tree = copy.deepcopy(original)
        data = tree['m_VertexData']
        require(data['m_VertexCount'] == 9 and len(data['m_DataSize']) == 288 and tree['m_StreamData']['size'] == 0, 'Unreviewed decal vertex layout')
        require(data['m_Channels'][0] == {'stream': 0, 'offset': 0, 'format': 0, 'dimension': 3}, 'Unreviewed position channel')
        vertices = bytearray(data['m_DataSize'])
        for i in range(9):
            y = struct.unpack_from('<f', vertices, i * 32 + 4)[0]
            require(y == original['m_LocalAABB']['m_Center']['y'], 'Decal is no longer planar')
            struct.pack_into('<f', vertices, i * 32 + 4, y + LIFT)
        data['m_DataSize'] = bytes(vertices)
        tree['m_LocalAABB']['m_Center']['y'] += LIFT
        tree['m_SubMeshes'][0]['localAABB']['m_Center']['y'] += LIFT
        # Reverse every allowed change to prove UVs, normals, indices and topology are exact.
        reversed_tree = copy.deepcopy(tree)
        reversed_tree['m_VertexData']['m_DataSize'] = original['m_VertexData']['m_DataSize']
        reversed_tree['m_LocalAABB'] = original['m_LocalAABB']
        reversed_tree['m_SubMeshes'][0]['localAABB'] = original['m_SubMeshes'][0]['localAABB']
        require(reversed_tree == original, 'Unexpected mesh changes')
        obj.save_typetree(tree)
        raw = file.save()
    finally:
        env.file.reader.dispose()
    check = UnityPy.load(raw)
    try:
        require(identity(check.file) == layout and set(check.file.objects) == set(before), 'Scene layout changed')
        changed = [i for i, o in check.file.objects.items() if before[i] != sha(o.get_raw_data())]
        require(changed == [67], 'Unrelated scene object changed')
        saved = check.file.objects[67].read_typetree()
        actual = saved['m_VertexData']['m_DataSize']
        for i in range(9):
            require(abs(struct.unpack_from('<f', actual, i * 32 + 4)[0] - (original['m_LocalAABB']['m_Center']['y'] + LIFT)) < 1e-7, 'Saved lift differs')
            require(actual[i*32:i*32+4] == vertices[i*32:i*32+4] and actual[i*32+8:i*32+32] == original['m_VertexData']['m_DataSize'][i*32+8:i*32+32], 'Non-Y vertex data changed')
    finally:
        check.file.reader.dispose()
    output.mkdir(parents=True, exist_ok=True)
    (output / 'level1').write_bytes(raw)
    receipt = dict(sourceSHA256=SOURCE_SHA, candidateSHA256=sha(raw), changedObjects=changed,
                   unchangedObjects=len(before)-1, meshVertices=9, localLift=LIFT, worldLiftMetres=LIFT*.14108194410800934)
    (output / 'logo-check.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    prepare(args.source.resolve(), args.output.resolve())
