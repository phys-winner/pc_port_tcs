"""Repair GLES per-texture builtin sampler arrays; preserve all compute code."""
from pathlib import Path
import argparse,copy,hashlib,json,re
import UnityPy

ROOT=Path(__file__).resolve().parents[1]
CANONICAL=ROOT/'app/obb/assets/bin/Data/resources.assets'
IDS={1823,1824,1825,1826,1827,1828,1830}
def digest(raw):return hashlib.sha256(raw).hexdigest()

def parallel_sampler_bindings(case, original):
    states={item['bindPoint']:item['sampler'] for item in original['builtinSamplers']}
    assert len(states)==len(original['builtinSamplers'])
    code=bytes(case['code']).decode('utf-8').rstrip('\0')
    parallel=[]; entries=[]
    for ti,texture in enumerate(case['textures']):
        semantic,generated=texture['name'],texture['generatedName']
        match=re.fullmatch(re.escape(semantic)+r'TEX_with_SMPsampler(\d+)',generated)
        if match:
            slot=int(match[1]);assert slot in states
            assert texture['samplerBindPoint']==slot
            state=states[slot];kind='combined_original_sampler_state'
        else:
            assert generated==semantic,'Unknown direct texture alias'
            uses=re.findall(r'\b(texture\w*|texelFetch\w*)\s*\(\s*'+re.escape(generated)+r'\b',code)
            assert all(name.startswith('texelFetch') or name in ('textureSize','textureQueryLevels') for name in uses),'Direct uniform samples need explicit sampler-state mapping'
            state=0xffffffff;kind='native_load_only_neutral_sampler'
        # Exact resourcesResolved=false native path initializes the
        # bindPoint word to0; the true path overwrites it with GL unit.
        parallel.append({'sampler':state,'bindPoint':0})
        entries.append({'textureIndex':ti,'semantic':semantic,'generatedName':generated,
                        'sampler':state,'kind':kind})
    return parallel, entries

def repaired(tree):
    target=copy.deepcopy(tree)
    source=next(v for v in tree['variants'] if v['targetRenderer']==2)
    gles=next(v for v in target['variants'] if v['targetRenderer']==11)
    assert gles['targetLevel']==3 and gles['resourcesResolved'] is True
    assert len(source['kernels'])==len(gles['kernels'])
    changes=[]
    for ki,(d3d,kernel) in enumerate(zip(source['kernels'],gles['kernels'])):
        assert d3d['name']==kernel['name']
        originals={key:case for key,case in d3d['variantMap']}
        for ci,(key,case) in enumerate(kernel['variantMap']):
            original=originals[key]
            parallel,entries=parallel_sampler_bindings(case,original)
            if parallel!=case['builtinSamplers']:
                changes.append({'kernelIndex':ki,'kernel':kernel['name'],'caseIndex':ci,'case':key,
                                'old':copy.deepcopy(case['builtinSamplers']),'new':parallel,
                                'textureCount':len(case['textures']),'codeSha256':digest(bytes(case['code'])),
                                'entries':entries})
                case['builtinSamplers']=parallel
            assert len(case['builtinSamplers'])==len(case['textures'])
    reverse=copy.deepcopy(target)
    reverse_gles=next(v for v in reverse['variants'] if v['targetRenderer']==11)
    for row in changes:
        reverse_gles['kernels'][row['kernelIndex']]['variantMap'][row['caseIndex']][1]['builtinSamplers']=row['old']
    assert reverse==tree,'A field outside the parallel builtin sampler arrays changed'
    return target,changes

def prepare(candidate):
    assert candidate.is_relative_to(ROOT/'work') and not candidate.exists()
    candidate.mkdir(parents=True)
    original_file=CANONICAL.read_bytes();env=UnityPy.load(original_file)
    before={o.path_id:digest(o.get_raw_data()) for o in env.objects};rows=[]
    for obj in env.objects:
        if obj.path_id not in IDS:continue
        assert obj.type.name=='ComputeShader'
        tree=obj.read_typetree();target,changes=repaired(tree)
        if not changes:continue
        old=obj.get_raw_data();raw=obj.save_typetree(target)
        rows.append({'pathId':obj.path_id,'name':tree['m_Name'],'oldSha256':digest(old),'newSha256':digest(raw),
                     'changes':changes,'objectFile':str(obj.path_id)+'.compute'})
    output=env.file.save();saved=UnityPy.load(output);objects={o.path_id:o for o in saved.objects}
    selected={row['pathId']:row for row in rows}
    for key,obj in objects.items():
        assert digest(obj.get_raw_data())==(selected[key]['newSha256'] if key in selected else before[key]),'Unrelated object changed'
        if key in selected:
            tree=obj.read_typetree();again,changes=repaired(tree)
            assert again==tree and not changes,'Repair is not idempotent'
            (candidate/selected[key]['objectFile']).write_bytes(obj.get_raw_data())
    (candidate/'resources.assets').write_bytes(output)
    manifest={'format':'closing-shift-compute-parallel-samplers-v1','file':'resources.assets',
              'sourceFileSha256':digest(original_file),'changedObjects':len(rows),
              'changedCases':sum(len(row['changes']) for row in rows),'objects':rows,
              'nativeProof':'GLES ResolveComputeProgramResources resourcesResolved=true writes builtinSamplers[textureIndex].bindPoint; false branch constructs {-1,0} per texture',
              'glslAndAllOtherFieldsPreserved':True,'unrelatedObjectsVerified':len(before)-len(rows),'canonicalWrites':False}
    (candidate/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:manifest[k] for k in ('changedObjects','changedCases','unrelatedObjectsVerified')},indent=2))

def apply(candidate,verify_only,evidence):
    manifest=json.loads((candidate/'manifest.json').read_text(encoding='utf-8'))
    assert manifest['format']=='closing-shift-compute-parallel-samplers-v1' and manifest['file']=='resources.assets'
    source=CANONICAL.read_bytes();env=UnityPy.load(source);objects={o.path_id:o for o in env.objects}
    before={key:digest(obj.get_raw_data()) for key,obj in objects.items()};changed={}
    for row in manifest['objects']:
        key=row['pathId'];assert key in IDS and objects[key].type.name=='ComputeShader'
        raw=(candidate/row['objectFile']).read_bytes();assert digest(raw)==row['newSha256']
        assert before[key] in (row['oldSha256'],row['newSha256']),'Current compute preimage differs'
        if before[key]==row['newSha256']:continue
        # Independently rederive the exact candidate from current metadata.
        target,changes=repaired(objects[key].read_typetree())
        assert changes==row['changes']
        derived=objects[key].save_typetree(target)
        assert digest(derived)==row['newSha256'],'Candidate does not match native-ABI repair'
        changed[key]=row
    output=env.file.save();check=UnityPy.load(output);saved={o.path_id:o for o in check.objects}
    assert saved.keys()==before.keys()
    for key,obj in saved.items():
        assert digest(obj.get_raw_data())==(changed[key]['newSha256'] if key in changed else before[key]),'Other Shader/texture/audio/localization object changed'
    for row in manifest['objects']:
        tree=saved[row['pathId']].read_typetree();assert repaired(tree)==(tree,[])
    if verify_only:
        print('VERIFY_PASS: all compute preimages, exact metadata derivation, unchanged GLSL and all unrelated raw objects; no writes')
        return
    assert CANONICAL.read_bytes()==source,'Canonical file changed concurrently'
    staging=CANONICAL.with_name(CANONICAL.name+'.compute-samplers.tmp')
    staging.write_bytes(output);assert staging.read_bytes()==output
    staging.replace(CANONICAL)
    persisted=UnityPy.load(CANONICAL.read_bytes());persisted_objects={o.path_id:o for o in persisted.objects}
    for row in manifest['objects']:assert digest(persisted_objects[row['pathId']].get_raw_data())==row['newSha256']
    report={'candidateManifestSha256':digest((candidate/'manifest.json').read_bytes()),'changedObjects':len(changed),
            'unchangedRawObjectsVerified':len(before)-len(changed),'deviceValidated':False,'canonicalPersistedVerified':True}
    if evidence:
        evidence=evidence.resolve();assert evidence.is_relative_to(ROOT/'work');evidence.parent.mkdir(parents=True,exist_ok=True)
        evidence.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report,indent=2))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate',type=Path,default=ROOT/'work/compute_sampler_bindings_candidate')
    mode=parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--prepare',action='store_true');mode.add_argument('--verify-only',action='store_true');mode.add_argument('--apply',action='store_true')
    parser.add_argument('--evidence',type=Path)
    args=parser.parse_args();candidate=args.candidate.resolve();assert candidate.is_relative_to(ROOT/'work')
    if args.prepare:prepare(candidate)
    else:apply(candidate,args.verify_only,args.evidence)
