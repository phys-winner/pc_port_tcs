"""Restore Unity camera-depth float precision; default is a work-only candidate.

Only the reserved _CameraDepthTexture sampler and direct sampled-value storage
qualifiers change. Packed _CameraDepthNormalsTexture, all math, native bindings,
render states, other backends and non-Shader objects remain exact.

Prepare: python scripts/prepare_depth_precision.py --output work/depth_precision_candidate
Check:   python scripts/prepare_depth_precision.py --output work/depth_precision_candidate --verify-only
Apply:   python scripts/prepare_depth_precision.py --output work/depth_precision_candidate --apply
Already-correct highp programs are accepted without rewriting their hashes or bytes.
"""
from pathlib import Path
import os
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
import argparse
import copy
import hashlib
import json
import re
import subprocess
import UnityPy
from unity_shader_blob import read_programs, encode_record
from shader_tail import parse_record
from prepare_sampler_locations import SAMPLER, split_stages, replace_backend
from filter_runtime_shader_backend import program_snapshot

ROOT = Path(__file__).resolve().parents[1]
FORMAT = 'closing-shift-camera-depth-precision-v1'
VALIDATOR = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/emulator/lib64/vulkan/glslangValidator.exe'
ORIGINAL = Path('external/game/TheClosingShift_Data')
CG = ROOT / 'work/tools/unity_builtin_shaders_2020.3.30f1/source/CGIncludes'
PINNED = (
    (ORIGINAL / 'globalgamemanagers', 'b40cd5e104e504409640daf9905ce920b87c9d039fc5b7393b2e6d73b854d29e'),
    (ORIGINAL / 'Resources/unity_builtin_extra', '5547bdf5549e667021375369371b8421b49a0f67f91aabba568b475583177059'),
    (CG / 'HLSLSupport.cginc', 'd6b6b58f74a14e0cd8db3e84bb418689f6bf332b7f32f6449d51a371d6e29ff8'),
    (CG / 'UnityDeferredLibrary.cginc', '3ceb58336ceb27f7401b2cf5d5301f3aaa6e521ac4ae659bd6582370ee4fb452'),
)
INPUTS = tuple(map(Path, (
    'obb/assets/bin/Data/resources.assets', 'obb/assets/bin/Data/sharedassets0.assets',
    'obb/assets/bin/Data/sharedassets1.assets', 'assets/bin/Data/globalgamemanagers.assets',
    'assets/bin/Data/unity default resources', 'assets/bin/Data/Resources/unity default resources',
    'assets/bin/Data/Resources/unity_builtin_extra')))
BLOB = {'compressedBlob', 'offsets', 'compressedLengths', 'decompressedLengths'}
STAGES = ('progVertex', 'progFragment', 'progGeometry', 'progHull', 'progDomain', 'progRayTracing')
IDENT = r'[A-Za-z_][A-Za-z_0-9]*'
READ = re.compile(r'(?P<dst>'+IDENT+r')(?P<dstswizzle>\.[xyzwrgba]+)? = '
    r'(?P<function>texture|textureLod)\(_CameraDepthTexture, (?P<args>.+)\)(?P<swizzle>\.[xyzwrgba]+)?;')


def digest(raw): return hashlib.sha256(raw).hexdigest()


def container_layout(file):
    return (file.unity_version, file.target_platform, file.header.version, file.header.endian,
        tuple((x.path,x.temp_empty,x.guid,x.type) for x in file.externals),
        {i:(o.type_id,o.class_id,o.is_destroyed,o.is_stripped) for i,o in file.objects.items()})


def provenance():
    rows = []
    for path, expected in PINNED:
        assert path.is_file() and digest(path.read_bytes()) == expected, 'Pinned original/Unity source changed: ' + str(path)
        rows.append({'path': str(path), 'sha256': expected})
    assert '#define UNITY_DECLARE_DEPTH_TEXTURE(tex) sampler2D_float tex' in (CG/'HLSLSupport.cginc').read_text(encoding='utf-8-sig')
    assert 'UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);' in (CG/'UnityDeferredLibrary.cginc').read_text(encoding='utf-8-sig')
    return rows


def patch_code(code):
    if b'_CameraDepthTexture' not in code:
        return code, None
    text = code.decode('utf-8'); vertex, fragment = split_stages(text)
    declarations = [m for m in SAMPLER.finditer(fragment) if m['name'] == '_CameraDepthTexture']
    if not declarations:
        assert not re.search(r'\b_CameraDepthTexture\b', text), 'Unparsed depth declaration/use'
        return code, None
    assert len(declarations) == 1 and '_CameraDepthTexture' not in vertex
    assert '#version 310 es\n' in fragment and 'precision highp float;\n' in fragment, 'Unreviewed generated shader dialect'
    uniform = declarations[0]
    assert uniform['type'] == 'sampler2D' and uniform['count'] is None and uniform['macro'] is not None, 'Comparison/array/unlocated depth sampler'
    assert re.fullmatch(r'uniform (?:mediump|highp) sampler2D _CameraDepthTexture;', uniform['declaration'])
    assert uniform.group(0) == f'UNITY_LOCATION({uniform["macro"]}) '+uniform['declaration']
    samples = []; receivers = set()
    for line in fragment.splitlines():
        if not re.search(r'\b(?:texture\w*|texelFetch\w*)\(_CameraDepthTexture\s*,', line): continue
        match = READ.fullmatch(line)
        assert match, 'Unknown direct camera-depth sampling expression: ' + line
        depth = 0
        for char in match['args']:
            depth += int(char == '(')-int(char == ')'); assert depth >= 0
        assert depth == 0 and ';' not in match['args'] and '_CameraDepthTexture' not in match['args']
        receivers.add(match['dst'])
        samples.append({'destination': match['dst'], 'destinationSwizzle': match['dstswizzle'],
                        'function': match['function'], 'resultSwizzle': match['swizzle']})
    assert samples and len(re.findall(r'\b_CameraDepthTexture\b', fragment)) == 1+len(samples), 'Unused/aliased/unparsed depth sampler'
    edits = []
    old_uniform = uniform.group(0)
    if ' mediump ' in old_uniform: edits.append((old_uniform, old_uniform.replace(' mediump ', ' highp ', 1)))
    storage = []
    for name in sorted(receivers):
        matches = list(re.finditer(r'(?m)^(?P<precision>mediump |highp )?(?P<type>float|vec[234]) '+re.escape(name)+r';$', fragment))
        assert len(matches) == 1, 'Ambiguous sampled-depth destination: ' + name
        match = matches[0]; old = match.group(0)
        storage.append({'name': name, 'type': match['type'], 'oldPrecision': match['precision'] or 'highp (default)'})
        if match['precision'] == 'mediump ': edits.append((old, old.replace('mediump ', 'highp ', 1)))
    new = fragment
    for old, value in edits:
        assert new.count(old+'\n') == 1
        new = new.replace(old+'\n', value+'\n', 1)
    delta = [(a,b) for a,b in zip(fragment.splitlines(), new.splitlines()) if a != b]
    assert len(fragment.splitlines()) == len(new.splitlines()) and len(delta) == len(edits)
    assert all(a.replace('mediump','highp',1) == b for a,b in delta)
    result = text.replace(fragment, new, 1).encode('utf-8'); reverse = result.decode()
    for old, value in reversed(edits):
        assert reverse.count(value+'\n') == 1
        reverse = reverse.replace(value+'\n', old+'\n', 1)
    assert reverse.encode() == code and split_stages(result.decode())[0] == vertex
    def abi(s): return [(m['name'],m['type'],m['count'],m['macro'],m['layout']) for m in SAMPLER.finditer(s)]
    assert abi(fragment) == abi(new), 'Native texture binding contract changed'
    return result, {'sampleCount':len(samples), 'samples':samples, 'storage':storage, 'qualifierEdits':len(edits)}


def update_editor_hashes(before, target, records, changed):
    edits = []
    for si,(a,b) in enumerate(zip(before['m_ParsedForm']['m_SubShaders'], target['m_ParsedForm']['m_SubShaders'])):
        for pi,(old,new) in enumerate(zip(a['m_Passes'], b['m_Passes'])):
            indices = {sp['m_BlobIndex'] for stage in STAGES for sp in old[stage]['m_SubPrograms'] if sp['m_GpuProgramType'] == 4}
            if not indices.intersection(changed): continue
            assert 9 in old['m_Platforms'] and len(old['m_Platforms']) == len(old['m_EditorDataHash'])
            index = old['m_Platforms'].index(9)
            h = hashlib.md5(b''.join(records[i] for i in sorted(indices))).digest()
            new['m_EditorDataHash'][index] = {f'bytes[{i}]':v for i,v in enumerate(h)}
            edits.append({'subshader':si, 'pass':pi, 'old':old['m_EditorDataHash'][index], 'new':new['m_EditorDataHash'][index]})
    assert edits
    reverse = copy.deepcopy(target)
    for row in edits:
        p = reverse['m_ParsedForm']['m_SubShaders'][row['subshader']]['m_Passes'][row['pass']]
        p['m_EditorDataHash'][p['m_Platforms'].index(9)] = row['old']
    assert {k:v for k,v in reverse.items() if k not in BLOB} == {k:v for k,v in before.items() if k not in BLOB}
    return edits


def repair_shader(tree, cache=None):
    if 9 not in tree['platforms']: return tree, [], [], {}, 0
    records = read_programs(tree,9); encoded = []; changes = []; programs = {}; checked = 0
    cache = {} if cache is None else cache
    for record in records:
        assert encode_record(record) == record['record_bytes']
        old = record['program_code']; identity = digest(old)
        if identity not in cache: cache[identity] = patch_code(old)
        code, info = cache[identity]; checked += int(info is not None)
        if code == old: encoded.append(record['record_bytes']); continue
        assert record['program_type'] == 4 and info['qualifierEdits']
        raw = encode_record({**record, 'program_code':code}); parsed = parse_record(raw)
        assert all(parsed[k] == record[k] for k in ('version','program_type','stats','keywords','local_keywords','tail'))
        assert raw[parsed['tail_offset']:] == record['tail_bytes']
        assert encode_record({**parsed,'program_code':old}) == record['record_bytes']
        programs[digest(code)] = code
        changes.append({'record':record['index'], 'oldCodeSha256':identity, 'newCodeSha256':digest(code),
            'oldRecordSha256':digest(record['record_bytes']), 'newRecordSha256':digest(raw),
            'tailSha256':digest(record['tail_bytes']), **info})
        encoded.append(raw)
    if not changes: return tree, [], [], {}, checked
    target = replace_backend(tree,encoded)
    hashes = update_editor_hashes(tree,target,encoded,{r['record'] for r in changes})
    for platform in tree['platforms']:
        if platform != 9: assert program_snapshot(tree,platform) == program_snapshot(target,platform)
    return target, changes, hashes, programs, checked


def validate(item, output):
    identity, code = item; folder = output/'validated'/identity; folder.mkdir(parents=True,exist_ok=True)
    vertex, fragment = split_stages(code.decode()); v,f = folder/'shader.vert',folder/'shader.frag'
    v.write_text(vertex); f.write_text(fragment)
    run = subprocess.run([str(VALIDATOR),'-l',str(v),str(f)],capture_output=True,text=True)
    (folder/'link.log').write_text(run.stdout+run.stderr)
    assert run.returncode == 0, str(folder)+'\n'+run.stdout+run.stderr
    return identity


def prepare(app, output):
    assert not output.exists(), 'Choose a fresh work output or use --verify-only/--apply with an existing candidate'
    proof = provenance(); assert VALIDATOR.is_file()
    output.mkdir(parents=True); (output/'objects').mkdir(); cache = {}; programs = {}; files = []
    for relative in INPUTS:
        path = app/relative
        if not path.is_file(): continue
        source = path.read_bytes(); env = UnityPy.load(source)
        layout = container_layout(env.file)
        before = {o.path_id:digest(o.get_raw_data()) for o in env.objects}; rows = []; checked = 0
        for obj in env.objects:
            if obj.type.name != 'Shader': continue
            tree = obj.read_typetree(); target, changes, hashes, code, count = repair_shader(tree,cache)
            checked += count
            # Exact Unity player default resources were built with2020.3.22f1;
            # they have no matching depth records and remain untouched.
            if count: assert env.file.unity_version == '2020.3.30f1', 'Unexpected depth-shader source version'
            if not changes: continue
            old = obj.get_raw_data(); raw = obj.save_typetree(target); name = path.name+'.'+str(obj.path_id)+'.shader'
            rows.append({'pathId':obj.path_id,'name':tree['m_ParsedForm']['m_Name'],'oldSha256':digest(old),
                'newSha256':digest(raw),'objectFile':'objects/'+name,'changes':changes,'editorHashes':hashes,
                'glesSnapshot':program_snapshot(target,9)})
            programs.update(code)
            print(path.name,obj.path_id,tree['m_ParsedForm']['m_Name'],'changed',len(changes),flush=True)
        if rows:
            container = output/'containers'/relative; container.parent.mkdir(parents=True,exist_ok=True)
            saved_bytes = env.file.save(); container.write_bytes(saved_bytes); saved = UnityPy.load(saved_bytes)
            assert container_layout(saved.file) == layout, 'Container headers, external PPtrs or object types changed'
            selected = {r['pathId']:r for r in rows}; assert set(before) == set(saved.file.objects)
            for key,obj in saved.file.objects.items():
                row = selected.get(key); raw = obj.get_raw_data()
                assert digest(raw) == (row['newSha256'] if row else before[key]), 'Unselected object changed'
                if row:
                    assert program_snapshot(obj.read_typetree(),9) == row['glesSnapshot']
                    assert repair_shader(obj.read_typetree())[1] == [], 'Repair is not idempotent'
                    (output/row['objectFile']).write_bytes(raw)
            saved.file.reader.dispose()
        files.append({'file':relative.as_posix(),'sourceFileSha256':digest(source),'shaders':rows,
            'depthRecordsChecked':checked,'unrelatedRawObjectsVerified':len(before)-len(rows)})
        env.file.reader.dispose()
    assert sum(f['depthRecordsChecked'] for f in files) > 0, 'No reviewed GLES camera-depth input; convert shaders first'
    validated = []
    with ThreadPoolExecutor(max_workers=2) as pool:
        for future in as_completed([pool.submit(validate,item,output) for item in programs.items()]):
            validated.append(future.result())
            if len(validated)%250 == 0: print('LINK_PASS',len(validated),'/',len(programs),flush=True)
    manifest = {'format':FORMAT,'pinnedInputs':proof,'files':files,'changedShaders':sum(len(f['shaders']) for f in files),
        'changedRecords':sum(len(s['changes']) for f in files for s in f['shaders']),
        'depthRecordsChecked':sum(f['depthRecordsChecked'] for f in files),'validatedPrograms':sorted(validated),
        'packedDepthNormalsExcluded':True,'canonicalWritten':False}
    (output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:manifest[k] for k in ('changedShaders','changedRecords','depthRecordsChecked')}))


def apply_or_verify(app, output, apply):
    proof = provenance(); manifest_bytes = (output/'manifest.json').read_bytes(); manifest = json.loads(manifest_bytes)
    assert manifest['format'] == FORMAT and manifest['pinnedInputs'] == proof and manifest['packedDepthNormalsExcluded']
    assert len(manifest['validatedPrograms']) == len(set(manifest['validatedPrograms']))
    planned = []; changed_count = 0; unrelated = 0
    for file in manifest['files']:
        relative = Path(file['file']); assert relative in INPUTS
        path = (app/relative).resolve(strict=True); assert path.is_relative_to(app)
        source = path.read_bytes(); env = UnityPy.load(source); objects = env.file.objects
        layout = container_layout(env.file)
        before = {i:digest(o.get_raw_data()) for i,o in objects.items()}; selected = {}; seen = set(); cache = {}
        for row in file['shaders']:
            key = row['pathId']; obj = objects[key]
            assert key not in seen and obj.type.name == 'Shader' and obj.read_typetree()['m_ParsedForm']['m_Name'] == row['name']
            seen.add(key)
            export = (output/row['objectFile']).resolve(strict=True); assert export.is_relative_to(output)
            raw = export.read_bytes(); assert digest(raw) == row['newSha256']
            assert before[key] in (row['oldSha256'],row['newSha256']), 'Selected Shader preimage changed'
            if before[key] == row['newSha256']:
                assert repair_shader(obj.read_typetree(),cache)[1] == []; continue
            target, changes, hashes, programs, _ = repair_shader(obj.read_typetree(),cache)
            assert json.loads(json.dumps(changes)) == row['changes'] and hashes == row['editorHashes']
            assert digest(obj.save_typetree(target)) == row['newSha256'], 'Raw export is not the exact semantic precision repair'
            for identity, code in programs.items():
                assert identity in manifest['validatedPrograms']; folder = output/'validated'/identity
                vertex, fragment = split_stages(code.decode())
                assert (folder/'shader.vert').read_text() == vertex and (folder/'shader.frag').read_text() == fragment
                assert 'ERROR:' not in (folder/'link.log').read_text()
            selected[key] = row
        saved_bytes = env.file.save() if selected else source; env.file.reader.dispose()
        check = UnityPy.load(saved_bytes); assert set(before) == set(check.file.objects)
        assert container_layout(check.file) == layout, 'Container layout or external PPtrs changed'
        for key,obj in check.file.objects.items():
            assert digest(obj.get_raw_data()) == (selected[key]['newSha256'] if key in selected else before[key]), 'Nonselected Shader/texture/audio/other bytes changed'
        for row in file['shaders']:
            obj = check.file.objects[row['pathId']]
            assert program_snapshot(obj.read_typetree(),9) == row['glesSnapshot'] and repair_shader(obj.read_typetree())[1] == []
        check.file.reader.dispose(); unrelated += len(before)-len(selected); changed_count += len(selected)
        if selected: planned.append((path,source,saved_bytes))
    # Preflight every container before the first write. Replacing a work-QA hardlink cannot alter its source.
    for path,source,_ in planned: assert path.read_bytes() == source, 'Input changed concurrently'
    if apply:
        for path,source,data in planned:
            assert path.read_bytes() == source, 'Input changed after preflight'
            temporary = path.with_name(path.name+'.depth-precision.tmp')
            temporary.write_bytes(data); assert temporary.read_bytes() == data
            temporary.replace(path); assert path.read_bytes() == data
    print(json.dumps({'mode':'APPLIED' if apply else 'VERIFIED_NO_WRITES','manifestSha256':digest(manifest_bytes),
        'selectedObjectsToChange':changed_count,'unrelatedRawObjectsVerified':unrelated,'writtenContainers':len(planned) if apply else 0}))


def self_check():
    code = ('#ifdef VERTEX\n#version 310 es\nvoid main(){gl_Position=vec4(0.0);}\n#endif\n'
        '#ifdef FRAGMENT\n#version 310 es\nprecision highp float;\n'
        'UNITY_LOCATION(0) uniform mediump sampler2D _CameraDepthTexture;\nmediump vec4 d;\n'
        'void main(){\nd = texture(_CameraDepthTexture, vec2(0.5));\n}\n#endif\n').encode()
    fixed, info = patch_code(code); assert info['qualifierEdits'] == 2 and patch_code(fixed)[0] == fixed
    component = code.replace(b'd = texture(_CameraDepthTexture, vec2(0.5));',b'd.x = textureLod(_CameraDepthTexture, vec2(0.5), 0.0).x;')
    assert patch_code(component)[1]['qualifierEdits'] == 2
    packed = code.replace(b'_CameraDepthTexture',b'_CameraDepthNormalsTexture'); assert patch_code(packed) == (packed,None)
    rejected = 0
    for bad in (code.replace(b'texture(',b'textureGrad('),code.replace(b'sampler2D _CameraDepthTexture',b'sampler2DShadow _CameraDepthTexture'),
                code.replace(b'_CameraDepthTexture;',b'_CameraDepthTexture[2];'),code.replace(b'mediump vec4 d;',b'mediump vec4 d;\nmediump vec4 d;'),
                code.replace(b'd = texture(_CameraDepthTexture, vec2(0.5));',b'd = texture(_CameraDepthTexture, vec2(0.5)).x + 1.0;')):
        try: patch_code(bad)
        except AssertionError: rejected += 1
        else: raise AssertionError('Unknown precision/sample shape was accepted')
    assert rejected == 5
    return {'validDirectRead':True,'validComponentLodRead':True,'alreadyHighpIdempotent':True,'packedNormalsUntouched':True,'unknownShapesRejected':rejected}


if __name__ == '__main__':
    if not __debug__: raise RuntimeError('Run without Python -O; source-shape assertions are required')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app',type=Path,default=ROOT/'app')
    parser.add_argument('--output',type=Path,default=ROOT/'work/depth_precision_candidate')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--apply',action='store_true'); mode.add_argument('--verify-only',action='store_true'); mode.add_argument('--self-check',action='store_true')
    args = parser.parse_args(); checks = self_check()
    if args.self_check: print(json.dumps(checks))
    else:
        app,output = args.app.resolve(strict=True),args.output.resolve()
        assert app == ROOT/'app' or app.is_relative_to(ROOT/'work'), 'App must be canonical or an explicit work-QA copy'
        assert output.is_relative_to(ROOT/'work') and not output.is_relative_to(app) and not app.is_relative_to(output)
        if args.apply or args.verify_only: apply_or_verify(app,output,args.apply)
        else: prepare(app,output)
