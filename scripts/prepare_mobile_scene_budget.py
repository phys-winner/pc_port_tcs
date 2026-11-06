"""Prepare isolated rev15 asset A/Bs; never modify canonical data or install them."""
from __future__ import annotations

import argparse
from collections import Counter
import copy
import json
import os
from pathlib import Path
import shutil

import UnityPy

from prepare_light_shadow_ab import digest, identity, require, sha
from unity_shader_blob import read_programs

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'work/a64-mobile-revision15/app'
DATA = Path('obb/assets/bin/Data')
INPUTS = {
    'level1': 'c1e4f862205276420b050490df9e1851ce132224011f67eb0ddcfc498d7233c1',
    'sharedassets1.assets': 'a7bf38a8a826c7862c2e7b751c3b1b9daba67fdf1774eab3233eaa5a7934cc8b',
    'sharedassets0.assets': '6b7e69c0fd8a88000bcde8843b6c35383042de76ab05c85cd69437c4fbff3326',
}
MODES = {'player-fill-shadow': 'level1', 'opaque-prop-instancing': 'sharedassets1.assets'}
PROPS = {309: 'cup', 414: 'whip_cream_canister1'}


def object_path(file, go_id):
    names, seen = [], set()
    while go_id:
        require(go_id not in seen, 'Cyclic GameObject hierarchy')
        seen.add(go_id)
        go = file.objects[go_id].read()
        names.append(go.m_Name)
        transforms = [file.objects[p.component.m_PathID] for p in go.m_Component
                      if file.objects[p.component.m_PathID].type.name in ('Transform', 'RectTransform')]
        require(len(transforms) == 1, 'Ambiguous GameObject Transform')
        parent = transforms[0].read().m_Father.m_PathID
        go_id = file.objects[parent].read().m_GameObject.m_PathID if parent else 0
    return '/'.join(reversed(names))


def instancing_proof(source):
    env = UnityPy.load(str(source / DATA / 'sharedassets0.assets'))
    try:
        shader = env.file.objects[798]
        require(sha(shader.get_raw_data()) == '402996b8b4d5709651e9fe734dfafe80a96bc455deafd651ded044352a2dbc63',
                'Standard798 shader preimage changed')
        tree = shader.read_typetree()
        records = read_programs(tree, 9)
        coverage = []
        for index, name, local, count in ((0, 'FORWARD', {'_METALLICGLOSSMAP', '_NORMALMAP'}, 62),
                                          (2, 'ShadowCaster', set(), 2)):
            shader_pass = tree['m_ParsedForm']['m_SubShaders'][0]['m_Passes'][index]
            require(shader_pass['m_State']['m_Name'] == name, 'Standard pass order changed')
            indices = {p['m_BlobIndex'] for p in shader_pass['progVertex']['m_SubPrograms'] if p['m_GpuProgramType'] == 4}
            selected = [records[i] for i in indices if set(records[i]['local_keywords']) == local]
            plain = {tuple(sorted(r['keywords'])) for r in selected if 'INSTANCING_ON' not in r['keywords']}
            instanced = {tuple(sorted(set(r['keywords']) - {'INSTANCING_ON'}))
                         for r in selected if 'INSTANCING_ON' in r['keywords']}
            require(len(plain) == count and plain == instanced, 'Instanced GLES keyword coverage is incomplete')
            coverage.append(dict(passName=name, matchedGlobalKeywordSets=count, localKeywords=sorted(local)))
    finally:
        env.file.reader.dispose()
    env = UnityPy.load(str(source / DATA / 'level1'))
    try:
        file = env.file
        meshes = {o.read().m_GameObject.m_PathID: o.read().m_Mesh.m_PathID
                  for o in file.objects.values() if o.type.name == 'MeshFilter'}
        groups = Counter()
        for obj in file.objects.values():
            if obj.type.name != 'MeshRenderer':
                continue
            renderer = obj.read()
            if not renderer.m_Enabled or renderer.m_StaticBatchInfo.subMeshCount or len(renderer.m_Materials) != 1:
                continue
            material = renderer.m_Materials[0]
            if material.m_FileID == 3 and material.m_PathID in PROPS:
                groups[(material.m_PathID, meshes.get(renderer.m_GameObject.m_PathID))] += 1
        require(groups == {(309, 1843): 46, (309, 1784): 20, (309, 2333): 18, (414, 2163): 8},
                'Repeated non-static prop mesh groups changed')
    finally:
        env.file.reader.dispose()
    return dict(shader798GLES=coverage, nonStaticMeshGroups=[dict(material=k[0], mesh=k[1], renderers=v)
                for k, v in sorted(groups.items())], includesInactiveRenderers=True,
                limitation='ForwardAdd passes do not instance; runtime draw reduction requires measurement')


def patch(raw, mode):
    filename = MODES[mode]
    require(sha(raw) == INPUTS[filename], filename + ' preimage changed')
    env = UnityPy.load(raw)
    try:
        file = env.file
        before = {i: sha(o.get_raw_data()) for i, o in file.objects.items()}
        layout = identity(file)
        changes = []
        for oid in ((33206,) if mode == 'player-fill-shadow' else PROPS):
            obj = file.objects[oid]
            tree = obj.read_typetree()
            original_tree, original_raw = copy.deepcopy(tree), obj.get_raw_data()
            if mode == 'player-fill-shadow':
                require(obj.type.name == 'Light' and tree['m_GameObject'] == {'m_FileID': 0, 'm_PathID': 8125},
                        'Player fill Light identity changed')
                require(object_path(file, 8125) == '-----------Main Elements---------------/Player/Point Light',
                        'Player fill hierarchy changed')
                require(tree['m_Type'] == 2 and tree['m_Enabled'] == 1 and tree['m_RenderMode'] == 0
                        and tree['m_Intensity'] == 0.05000000074505806 and tree['m_Range'] == 2.299999952316284
                        and tree['m_Shadows']['m_Type'] == 2, 'Player fill Light properties changed')
                tree['m_Shadows']['m_Type'] = 0
                field, old, new = 'm_Shadows.m_Type', 2, 0
            else:
                require(obj.type.name == 'Material' and tree['m_Name'] == PROPS[oid]
                        and tree['m_Shader'] == {'m_FileID': 5, 'm_PathID': 798}
                        and not tree['m_EnableInstancingVariants'], 'Prop Material identity changed')
                floats = dict(tree['m_SavedProperties']['m_Floats'])
                require(tree['m_CustomRenderQueue'] == -1 and floats['_Mode'] == 0 and floats['_SrcBlend'] == 1
                        and floats['_DstBlend'] == 0 and floats['_ZWrite'] == 1
                        and set(tree['m_ShaderKeywords'].split()) == {'_METALLICGLOSSMAP', '_NORMALMAP'},
                        'Prop is no longer an opaque material with the verified variants')
                tree['m_EnableInstancingVariants'] = True
                field, old, new = 'm_EnableInstancingVariants', 0, 1
            modified = obj.save_typetree(tree)
            delta = [i for i, (a, b) in enumerate(zip(original_raw, modified)) if a != b]
            require(len(modified) == len(original_raw) and len(delta) == 1
                    and original_raw[delta[0]] == old and modified[delta[0]] == new,
                    'Patch is not the single intended byte')
            if mode == 'player-fill-shadow':
                tree['m_Shadows']['m_Type'] = old
            else:
                tree['m_EnableInstancingVariants'] = bool(old)
            require(tree == original_tree, 'Unrelated serialized fields changed')
            changes.append(dict(pathId=oid, field=field, byteOffset=delta[0], old=old, new=new,
                                beforeSha256=before[oid], afterSha256=sha(modified)))
        result = file.save()
        check = UnityPy.load(result)
        try:
            require(identity(check.file) == layout and check.file.objects.keys() == before.keys(), 'Container identity changed')
            expected = {c['pathId']: c['afterSha256'] for c in changes}
            for oid, obj in check.file.objects.items():
                require(sha(obj.get_raw_data()) == expected.get(oid, before[oid]), 'Unrelated object changed: ' + str(oid))
        finally:
            check.file.reader.dispose()
        return result, dict(mode=mode, filename=filename, sourceSHA256=sha(raw), outputSHA256=sha(result),
                            changes=changes, unrelatedObjectsPreserved=len(before) - len(changes))
    finally:
        env.file.reader.dispose()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE, help='Existing staged app under work with exact rev15 assets')
    parser.add_argument('--mode', choices=MODES)
    parser.add_argument('--output', type=Path, help='New experiment directory under work; contains app/ and receipt')
    parser.add_argument('--self-test', action='store_true', help='Verify both modes and negative guards without writing files')
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    require(source.is_relative_to(ROOT / 'work') and source.is_dir()
            and not args.source.is_symlink() and not args.source.is_junction(), 'Source must be a plain work app')
    for name, expected in INPUTS.items():
        require(digest(source / DATA / name) == expected, name + ' input preimage changed')
    proof = instancing_proof(source)
    if args.self_test:
        reports = []
        for mode, name in MODES.items():
            raw = (source / DATA / name).read_bytes()
            result, report = patch(raw, mode)
            for invalid in (result, raw[:-1]):
                try:
                    patch(invalid, mode)
                except ValueError as error:
                    require('preimage' in str(error), 'Unexpected negative-guard failure')
                else:
                    raise AssertionError('Modified/truncated input accepted')
            require((source / DATA / name).read_bytes() == raw, 'Self-test changed source')
            reports.append(report)
        print(json.dumps(dict(status='MOBILE_SCENE_BUDGET_SELFTEST_PASS', reports=reports,
                              instancingProof=proof, sourceUnchanged=True), indent=2))
        return
    require(args.mode and args.output, '--mode and --output are required outside self-test')
    output = args.output.resolve()
    require(output.is_relative_to(ROOT / 'work') and not output.is_relative_to(source)
            and not source.is_relative_to(output) and not output.exists(), 'Output must be a new independent work directory')
    paths = list(source.rglob('*'))
    require(all(not p.is_symlink() and not p.is_junction() for p in paths), 'Source contains reparse paths')
    original = {p.relative_to(source).as_posix(): digest(p) for p in paths if p.is_file()}
    require(all(original[(DATA / name).as_posix()] == expected for name, expected in INPUTS.items()),
            'Source assets changed after proof')
    target = DATA / MODES[args.mode]
    raw = (source / target).read_bytes()
    result, report = patch(raw, args.mode)
    destination = output / 'app'
    shutil.copytree(source, destination, copy_function=os.link)
    path = destination / target
    temporary = path.with_name(path.name + '.mobile-budget.tmp')
    temporary.write_bytes(result)
    require(temporary.read_bytes() == result and path.read_bytes() == raw, 'Temporary write or source race')
    temporary.replace(path)
    actual = {p.relative_to(destination).as_posix(): digest(p) for p in destination.rglob('*') if p.is_file()}
    require(actual.keys() == original.keys() and {k for k in actual if actual[k] != original[k]} == {target.as_posix()},
            'Unexpected changed files in staged A/B')
    require({p.relative_to(source).as_posix(): digest(p) for p in paths if p.is_file()} == original, 'Source changed')
    report.update(source=str(source), outputApp=str(destination), sourceUnchanged=True, canonicalOrDeviceModified=False,
                  sourceFiles=original, outputFiles=actual, instancingProof=proof if args.mode == 'opaque-prop-instancing' else None)
    (output / 'scene-budget.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('MOBILE_SCENE_BUDGET_READY ' + str(destination))


if __name__ == '__main__':
    main()
