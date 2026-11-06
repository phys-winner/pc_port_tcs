"""Prepare/apply the accepted Aura grid delta; preserves every other raw object."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import UnityPy

ROOT=Path(__file__).resolve().parents[1]
SOURCE=Path('external/game/TheClosingShift_Data/level1')
OBJECT_ID=36456
OLD_SHA='9f4056b9718898c9092f4ad6307d244ca0833d9428775b7805a067d39de08cc8'
NEW_SHA='d10e5288654d8b70e1b77a2ad800ad0bf99f256f94e7f41f730eb8ad8b9ce2d3'

def require(condition,message):
    if not condition:raise ValueError(message)

def sha(raw):return hashlib.sha256(raw).hexdigest()

def delta(raw):
    require(len(raw)==196 and sha(raw)==OLD_SHA,'Exact original AuraQualitySettings raw preimage required')
    require(struct.unpack_from('<iii',raw,68)==(160,88,128),'Original grid differs')
    require(struct.unpack_from('<f',raw,84)==(128.0,),'Original far distance differs')
    result=raw[:68]+struct.pack('<iii',80,48,96)+raw[80:]
    require(sha(result)==NEW_SHA and result[:68]==raw[:68] and result[80:]==raw[80:],
            'Unexpected bytes outside authorized grid field')
    return result

def close(env):
    env.file.reader.dispose()

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply',action='store_true',help='Atomically update one raw object, or verify already applied')
    parser.add_argument('--data',type=Path,default=ROOT/'app/obb/assets/bin/Data')
    parser.add_argument('--output',type=Path,default=ROOT/'work/mobile_volume_quality')
    args=parser.parse_args()
    output=args.output.resolve()
    require(output.is_relative_to(ROOT/'work') and output!=ROOT/'work','Candidate reports must stay in a work subdirectory')
    original=UnityPy.load(str(SOURCE))
    try:
        before=original.file.objects[OBJECT_ID].get_raw_data()
        require(original.file.objects[OBJECT_ID].type.name=='MonoBehaviour','Source object type differs')
        candidate=delta(before)
    finally:close(original)
    output.mkdir(parents=True,exist_ok=True)
    raw_path=output/'level1_36456.raw';raw_path.write_bytes(candidate)
    receipt={'format':'closing-shift-accepted-aura-grid-v1','container':'level1','pathId':OBJECT_ID,
        'oldRawSha256':OLD_SHA,'newRawSha256':NEW_SHA,'rawCandidate':str(raw_path),
        'field':'frustumGridResolution','offset':68,'bytes':12,'before':[160,88,128],'after':[80,48,96],
        'farDistanceUnchanged':128,'allOtherObjectBytesPreserved':True,
        'acceptedNativeEvidence':'run0030: root measured last8 mean19.7613FPS vs17.087; all features retained',
        'ssr':'Original Overkill6 unchanged','mobileRender':'Accepted720 unchanged','managedDllsChanged':False,
        'applied':False,'deviceTouched':False}
    if args.apply:
        data=args.data.resolve(strict=True)
        require(data.is_relative_to(ROOT/'app') or data.is_relative_to(ROOT/'work'),'Apply target must stay inside workspace app/work')
        path=data/'level1'
        require(path.is_file() and not path.is_symlink(),'Expected ordinary level1 file')
        file_before=sha(path.read_bytes())
        env=UnityPy.load(str(path))
        temporary=path.with_name(path.name+'.mobile-volume-quality.tmp')
        made_temporary=False
        try:
            obj=env.file.objects[OBJECT_ID]
            raw=obj.get_raw_data()
            require(sha(raw) in (OLD_SHA,NEW_SHA),'Current target Aura raw is neither original nor accepted')
            if sha(raw)==NEW_SHA:
                receipt.update(status='ALREADY_APPLIED',inputSha256=file_before,outputSha256=file_before)
            else:
                require(not temporary.exists(),'Temporary output already exists; inspect it before retry')
                snapshots={pid:sha(item.get_raw_data()) for pid,item in env.file.objects.items()}
                platform=env.file.target_platform
                obj.set_raw_data(candidate)
                temporary.write_bytes(env.file.save());made_temporary=True
                check=UnityPy.load(str(temporary))
                try:
                    require(check.file.target_platform==platform,'Serialized platform changed')
                    require(set(check.file.objects)==set(snapshots),'Object ID set changed')
                    for pid,item in check.file.objects.items():
                        require(sha(item.get_raw_data())==(NEW_SHA if pid==OBJECT_ID else snapshots[pid]),
                                'Unrelated raw object changed: '+str(pid))
                finally:close(check)
                close(env);env=None
                require(sha(path.read_bytes())==file_before,'Target changed concurrently; refusing replacement')
                temporary.replace(path);made_temporary=False
                receipt.update(status='APPLIED',applied=True,inputSha256=file_before,outputSha256=sha(path.read_bytes()),
                    preservedRawObjects=len(snapshots)-1)
        finally:
            if env is not None:close(env)
            if made_temporary:temporary.unlink()
    else:receipt['status']='PREPARED_ONLY'
    (output/'manifest.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
    print(receipt['status']+' Aura grid80x48x96; far128; other raw fields/features preserved')

if __name__=='__main__':main()
