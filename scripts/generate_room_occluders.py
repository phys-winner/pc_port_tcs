"""Bake only the opaque triangles actually drawn by the cafe's static wall renderers."""
from pathlib import Path
import argparse
import hashlib
import json

import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
from UnityPy.streams import EndianBinaryReader

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'app/obb/assets/bin/Data'
OUTPUT = ROOT / 'scripts/patch_managed/AndroidRoomOccluders.cs'
WALLS = {'Store props/cafe/building4', 'Store props/cafe/cafe/Object767',
         'Store props/cafe/drivethru/wall'}
MIN_AREA = .5


def sha(data):
    return hashlib.sha256(data).hexdigest()


def vector(tree):
    return np.array([tree[k] for k in ('x', 'y', 'z')], dtype=np.float64)


def local_matrix(tree):
    x, y, z, w = [tree['m_LocalRotation'][k] for k in ('x', 'y', 'z', 'w')]
    assert abs(x*x+y*y+z*z+w*w-1) < 1e-5, 'Nonunit scene quaternion'
    result = np.eye(4)
    result[:3, :3] = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                              [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                              [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]]) @ np.diag(vector(tree['m_LocalScale']))
    result[:3, 3] = vector(tree['m_LocalPosition'])
    return result


def bake():
    environments, inputs = {}, {}

    def load(name):
        name = Path(name.replace('Library/', 'Resources/')).as_posix()
        if name not in environments:
            path = next((p for p in (DATA / name, ROOT / 'app/assets/bin/Data' / name) if p.is_file()), None)
            assert path is not None, 'Missing asset dependency: ' + name
            environments[name] = UnityPy.load(str(path))
        return environments[name].file

    def resolve(file, ptr):
        assert ptr['m_PathID'] and 0 <= ptr['m_FileID'] <= len(file.externals)
        target = file if not ptr['m_FileID'] else load(file.externals[ptr['m_FileID']-1].path)
        return target, target.objects[ptr['m_PathID']]

    def proof(obj):
        key = obj.assets_file.name + ':' + str(obj.path_id)
        inputs[key] = {'type': obj.type.name, 'sha256': sha(obj.get_raw_data())}

    def opaque(file, ptr):
        file, obj = resolve(file, ptr)
        assert obj.type.name == 'Material'
        tree = obj.read_typetree()
        floats = dict(tree['m_SavedProperties']['m_Floats'])
        assert tree['m_CustomRenderQueue'] <= 2450
        assert all(floats.get(k) == v for k, v in (('_Mode', 0), ('_SrcBlend', 1), ('_DstBlend', 0), ('_ZWrite', 1))), 'Wall is not opaque'
        assert not any(k in tree['m_ShaderKeywords'].split() for k in ('_ALPHATEST_ON', '_ALPHABLEND_ON', '_ALPHAPREMULTIPLY_ON'))
        shader_file, shader_obj = resolve(file, tree['m_Shader'])
        shader = shader_obj.read_typetree()['m_ParsedForm']
        assert shader['m_Name'] in ('Standard', 'UVFree/StandardMetallic/Single-Texture'), 'Unreviewed wall shader'
        state = next(p['m_State'] for p in shader['m_SubShaders'][0]['m_Passes'] if p['m_State']['m_Name'] == 'FORWARD')

        def value(prop):
            return floats.get(prop['name'], prop['val'])

        assert value(state['zWrite']) == 1 and value(state['culling']) == 2
        assert value(state['rtBlend0']['srcBlend']) == 1 and value(state['rtBlend0']['destBlend']) == 0
        proof(obj)
        proof(shader_obj)
        return {'name': tree['m_Name'], 'shader': shader['m_Name']}

    try:
        scene = load('level1')
        scene_env = environments['level1']
        stream = DATA / 'level1.resS'
        scene_env.register_cab('level1.resS.resource', EndianBinaryReader(open(stream, 'rb')))
        gos = {obj.path_id: obj.read_typetree() for obj in scene.objects.values() if obj.type.name == 'GameObject'}
        transforms = {obj.path_id: obj for obj in scene.objects.values() if obj.type.name in ('Transform', 'RectTransform')}
        by_go = {obj.read_typetree()['m_GameObject']['m_PathID']: obj for obj in transforms.values()}
        paths, matrices = {}, {}

        def hierarchy(go):
            if go not in paths:
                transform = by_go[go].read_typetree()
                parent = transform['m_Father']['m_PathID']
                parent_go = transforms[parent].read_typetree()['m_GameObject']['m_PathID'] if parent else 0
                paths[go] = (hierarchy(parent_go) + '/' if parent_go else '') + gos[go]['m_Name']
                matrices[go] = (matrices[parent_go] if parent_go else np.eye(4)) @ local_matrix(transform)
            return paths[go]

        filters = {obj.read_typetree()['m_GameObject']['m_PathID']: obj for obj in scene.objects.values() if obj.type.name == 'MeshFilter'}
        mesh_cache = {}
        owners = []
        for obj in scene.objects.values():
            if obj.type.name != 'MeshRenderer':
                continue
            renderer = obj.read_typetree()
            go = renderer['m_GameObject']['m_PathID']
            path = hierarchy(go)
            if path not in WALLS and not path.startswith('Store props/additional_room/'):
                continue
            batch = renderer['m_StaticBatchInfo']
            if not batch['subMeshCount']:
                continue
            assert not renderer['m_StaticBatchRoot']['m_PathID'], 'Only scene-root static batches have world-space vertices'
            mesh_file, mesh_obj = resolve(scene, filters[go].read_typetree()['m_Mesh'])
            key = mesh_file.name, mesh_obj.path_id
            if key not in mesh_cache:
                mesh = mesh_obj.read()
                handler = MeshHandler(mesh)
                handler.process()
                mesh_cache[key] = mesh, np.asarray(handler.m_Vertices, dtype=np.float64), handler.get_triangles()
                proof(mesh_obj)
                if mesh.m_StreamData and mesh.m_StreamData.size:
                    info = mesh.m_StreamData
                    assert info.path == 'level1.resS.resource'
                    with stream.open('rb') as source:
                        source.seek(info.offset)
                        raw = source.read(info.size)
                    assert len(raw) == info.size
                    inputs['level1.resS:' + str(info.offset)] = {'bytes': info.size, 'sha256': sha(raw)}
            mesh, vertices, submeshes = mesh_cache[key]
            assert batch['subMeshCount'] == len(renderer['m_Materials'])
            materials = [opaque(scene, ptr) for ptr in renderer['m_Materials']]
            triangles = []
            for submesh in range(batch['firstSubMesh'], batch['firstSubMesh'] + batch['subMeshCount']):
                assert mesh.m_SubMeshes[submesh].baseVertex == 0
                for indices in submeshes[submesh]:
                    triangle = vertices[list(indices)]
                    normal = np.cross(triangle[1]-triangle[0], triangle[2]-triangle[0])
                    area = np.linalg.norm(normal) / 2
                    if area >= MIN_AREA and abs(normal[1]) <= .2 * np.linalg.norm(normal):
                        triangles.append(triangle.tolist())
            if not triangles:
                continue
            proof(obj)
            proof(filters[go])
            ancestor = go
            while ancestor:
                proof(by_go[ancestor])
                parent = by_go[ancestor].read_typetree()['m_Father']['m_PathID']
                ancestor = transforms[parent].read_typetree()['m_GameObject']['m_PathID'] if parent else 0
            owners.append({'path': path, 'mesh': mesh.m_Name, 'vertexCount': mesh.m_VertexData.m_VertexCount,
                           'firstSubMesh': batch['firstSubMesh'], 'subMeshCount': batch['subMeshCount'],
                           'materials': materials, 'matrix': matrices[go].tolist(), 'triangles': triangles})
        assert WALLS - {'Store props/cafe/cafe/Object767'} <= {row['path'] for row in owners}, 'Cafe wall selection incomplete'
        owners.sort(key=lambda row: (row['path'], row['mesh']))
        assert len({(row['path'], row['mesh']) for row in owners}) == len(owners), 'Ambiguous wall identity'
        return owners, inputs
    finally:
        for environment in environments.values():
            environment.file.reader.dispose()


def literal(value):
    assert np.isfinite(value)
    result = format(np.float32(value), '.9g')
    return result + 'f'


def source_text(owners, inputs):
    lines = ['// Generated by scripts/generate_room_occluders.py; exact opaque rendered submeshes.',
             '// Source objects and stream ranges SHA256: ' + sha(json.dumps(inputs, sort_keys=True).encode()),
             'using UnityEngine;', '', 'namespace ClosingShift.Android', '{',
             '    internal static class AndroidRoomOccluders', '    {',
             '        internal static readonly AndroidRoomCulling.Source[] Sources = {']
    for owner in owners:
        quote = lambda value: json.dumps(value, ensure_ascii=True)
        lines.append('            new AndroidRoomCulling.Source(' + quote(owner['path']) + ', ' + quote(owner['mesh']) + ', ' + str(owner['vertexCount']) + ',')
        lines.append('                new float[] { ' + ', '.join(literal(value) for row in owner['matrix'] for value in row) + ' },')
        lines.append('                new string[] { ' + ', '.join(quote(row['name']) for row in owner['materials']) + ' },')
        lines.append('                new Vector3[] {')
        for triangle in owner['triangles']:
            lines.append('                    ' + ', '.join('new Vector3(' + ', '.join(map(literal, point)) + ')' for point in triangle) + ',')
        lines.extend(['                }),'])
    lines.extend(['        };', '    }', '}', ''])
    return '\n'.join(lines)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--verify', action='store_true', help='Reject stale generated geometry without writing files')
    parser.add_argument('--evidence', type=Path, help='Work-only provenance report; does not modify game assets')
    args = parser.parse_args()
    owners, inputs = bake()
    source = source_text(owners, inputs)
    if args.verify:
        assert OUTPUT.read_text(encoding='utf-8') == source, 'Room occluder source is stale; regenerate from reviewed current assets'
    else:
        OUTPUT.write_text(source, encoding='utf-8')
    if args.evidence:
        output = args.evidence.resolve()
        assert output.is_relative_to(ROOT / 'work')
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps({'sources': owners, 'inputs': inputs, 'minimumTriangleArea': MIN_AREA,
                                     'generatedSourceSha256': sha(source.encode()), 'canonicalAssetsChanged': False}, indent=2) + '\n', encoding='utf-8')
    print('ROOM_OCCLUDERS_PASS', len(owners), 'renderers', sum(len(row['triangles']) for row in owners), 'triangles', 'verify' if args.verify else 'generated')
