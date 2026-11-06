"""Prepare two original 2K counter mips in work; never change app or install."""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.metadata
import json
from pathlib import Path

import numpy as np
import texture2ddecoder
import UnityPy
from UnityPy.enums import BuildTarget
from UnityPy.export.Texture2DConverter import parse_image_data

from convert_textures import copy_range, encode_texture, range_sha, sha
from prepare_light_shadow_ab import identity, require
from prepare_mobile_textures import mip_sizes

ROOT = Path(__file__).resolve().parents[1]
AUDIT = ROOT / 'work/evidence/mobile_revision16/texture-audit'
PC = Path('external/game/TheClosingShift_Data')
ASSET, STREAM = 'sharedassets1.assets', 'sharedassets1.assets.resS'
TARGETS = (1186, 953)
GUARDS = {
    ASSET: 'a7bf38a8a826c7862c2e7b751c3b1b9daba67fdf1774eab3233eaa5a7934cc8b',
    STREAM: 'ba2a8f3098e05856aa7c530b9333e7dd236a1f2a2d89c5a6fca2bbef8c0a8d19',
}
PC_STREAM_SHA = '517158b484daa576b041c20be74e207d2e8f2c44c897ff45926e3a79b5b5c230'
HELPER_SHA = 'bd47aef94cb9430419f927283975134bc3abfb665266d5cef86449d33b7fead8'
RECEIPTS = {
    'cafe-bounded-selection.json': '86c8e6ebf88b0fbafc6e1219b3c0f623e85e07dee4ad9cb1785a39e6c76e4022',
    'cafe-encoder-append-plan.json': '5e9139778732aa9ef22b7773329c28f408cf971f2973e8feea5d7d41cea0a695',
    'protected-texture-references.json': '1999f7151bc229390c66b5352c29262ab5c06e9fbd50407f6d9171d0b025b233',
}
ALLOWED = {'m_Width', 'm_Height', 'm_MipCount', 'm_CompleteImageSize', 'm_StreamData'}


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def patch_metadata(raw, plans):
    require(digest(raw) == GUARDS[ASSET], 'Metadata preimage changed')
    env = UnityPy.load(raw)
    before = {i: (o.type.name, digest(o.get_raw_data())) for i, o in env.file.objects.items()}
    original_identity = identity(env.file)
    changes, expected = [], {}
    for row in plans:
        pid = row['pathId']; require(pid in TARGETS, 'Unexpected texture ID')
        obj = env.file.objects[pid]; old = obj.read_typetree()
        require(before[pid] == ('Texture2D', row['oldRawSha256']), 'Texture object preimage changed')
        require(old['m_StreamData'] == row['oldStream'] and old['m_StreamData']['path'] == STREAM+'.resource',
                'Native resource alias or current stream range changed')
        require((old['m_Width'], old['m_Height'], old['m_MipCount'], old['m_TextureFormat'],
                 old['m_CompleteImageSize'], old['m_ImageCount'], old['m_TextureDimension']) ==
                (1024, 1024, 11, 56, 626288, 1, 2), 'Texture layout differs')
        require(not old['m_IsReadable'] and old['m_StreamingMipmaps'] and old['m_ColorSpace'] == (1 if pid == 1186 else 0),
                'Readability, streaming or color-space contract changed')
        require(obj.save_typetree(copy.deepcopy(old)) == obj.get_raw_data(), 'Texture preimage does not roundtrip exactly')
        new = copy.deepcopy(old)
        new.update(m_Width=2048, m_Height=2048, m_MipCount=12, m_CompleteImageSize=row['newSize'])
        new['m_StreamData'].update(offset=row['newOffset'], size=row['newSize'])
        require(sum(mip_sizes(new)) == row['newSize'] and mip_sizes(new)[0] == row['prefixBytes'], 'ASTC mip byte arithmetic differs')
        require({k:v for k,v in old.items() if k not in ALLOWED} == {k:v for k,v in new.items() if k not in ALLOWED},
                'Unrelated texture field changed')
        modified = obj.save_typetree(new)
        require(len(modified) == len(obj.get_raw_data()), 'Texture object byte length changed')
        expected[pid] = new
        changes.append(dict(pathId=pid, name=old['m_Name'], oldRawSha256=before[pid][1], newRawSha256=digest(modified),
                            oldStream=old['m_StreamData'], newStream=new['m_StreamData'], allowedFields=sorted(ALLOWED)))
    require(set(expected) == set(TARGETS), 'Must patch exactly the two reviewed maps')
    result = env.file.save()
    check = UnityPy.load(result)
    require(identity(check.file) == original_identity and check.file.objects.keys() == before.keys(), 'Serialized identity changed')
    new_hashes = {r['pathId']:r['newRawSha256'] for r in changes}
    for pid,obj in check.file.objects.items():
        require(obj.type.name == before[pid][0], 'Object type changed')
        require(digest(obj.get_raw_data()) == new_hashes.get(pid, before[pid][1]), 'Unrelated raw object changed: '+str(pid))
        if pid in expected:
            require(obj.read_typetree() == expected[pid], 'Reparsed texture fields differ')
    return result, changes, len(before)-len(changes)


def quality_metrics(pid, original, decoded):
    require(original.shape == decoded.shape and original.ndim == 3 and original.shape[2] == 4, 'Decoded image shape mismatch')
    delta = original.astype(np.float32)-decoded.astype(np.float32)
    mse = np.mean(delta*delta, axis=(0,1), dtype=np.float64)
    result = dict(mseRGBA=mse.tolist(), rmseRGBA=np.sqrt(mse).tolist(),
                  maxAbsoluteErrorRGBA=np.max(np.abs(delta), axis=(0,1)).tolist())
    if pid == 1186:
        require(np.all(original[:,:,3] == 255) and np.all(decoded[:,:,3] == 255), 'Opaque albedo alpha changed')
        require(np.all(mse[:3] <= 400), 'Albedo RGB RMSE exceeds20/255')
        result['opaqueAlphaExact'] = True
    else:
        require(pid == 953 and np.all(original[:,:,0] == 255), 'Original DXT5 normal packing changed')
        # ASTC is lossy even for a source constant channel. Unity's actual
        # UnpackNormalmapRGorAG multiplies R*A; measure that decoded normal,
        # instead of silently assuming compressed R remains exactly255.
        require(mse[0] <= 4 and decoded[:,:,0].min() >= 239, 'Normal R channel lost its near-one packing contract')
        require(mse[1] <= 256 and mse[3] <= 256, 'Normal G/A channel RMSE exceeds16/255')
        def normal(pixels):
            p = pixels.astype(np.float32)/255
            xy = np.stack([p[:,:,0]*p[:,:,3],p[:,:,1]],axis=2)*2-1
            z = np.sqrt(np.maximum(0, 1-np.sum(xy*xy, axis=2)))
            v = np.dstack([xy,z])
            return v/np.linalg.norm(v,axis=2)[:,:,None]
        a,b = normal(original),normal(decoded)
        angles = np.degrees(np.arccos(np.clip(np.sum(a*b,axis=2),-1,1)))
        result.update(normalMeanDegrees=float(angles.mean()), normalP95Degrees=float(np.percentile(angles,95)),
                      normalP99Degrees=float(np.percentile(angles,99)), normalMaxDegrees=float(angles.max()),
                      originalR255Exact=True, decodedRedMin=int(decoded[:,:,0].min()),
                      decodedRedNot255Fraction=float(np.mean(decoded[:,:,0] != 255)),
                      normalPacking='authored DXT5 input unchanged; actual Unity decode x=R*A,y=G; no BC5 repack')
        require(result['normalMeanDegrees'] <= 4 and result['normalP95Degrees'] <= 12,
                'Normal angular error exceeds mean4deg/p95 12deg')
    require(all(np.isfinite(mse)), 'Nonfinite decoder metric')
    return result


def self_checks(raw, plans):
    patched, _, _ = patch_metadata(raw, plans)
    for bad in (raw[:-1], patched):
        try:
            patch_metadata(bad, plans)
        except ValueError as error:
            require('preimage' in str(error), 'Negative preimage guard failed unexpectedly')
        else:
            raise AssertionError('Modified/truncated metadata accepted')
    opaque = np.full((8,8,4), 255, np.uint8)
    require(quality_metrics(1186,opaque,opaque)['mseRGBA'] == [0]*4, 'Lossless albedo control failed')
    normal = opaque.copy(); normal[:,:,1] = 190; normal[:,:,3] = 64
    require(quality_metrics(953,normal,normal)['normalMeanDegrees'] < .1, 'Lossless normal control failed')
    for pid, original, bad in ((1186,opaque,opaque.copy()), (953,normal,normal.copy())):
        bad[:,:,(3 if pid == 1186 else 0)] = 0
        try:
            quality_metrics(pid,original,bad)
        except ValueError:
            pass
        else:
            raise AssertionError('Broken alpha/normal packing accepted')
    swapped = normal.copy(); swapped[:,:,1],swapped[:,:,3] = normal[:,:,3],normal[:,:,1]
    try:
        quality_metrics(953,normal,swapped)
    except ValueError:
        pass
    else:
        raise AssertionError('Swapped normal X/Y channels accepted')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,default=ROOT/'work/a64-mobile-revision15/app/obb/assets/bin/Data')
    parser.add_argument('--output',type=Path,default=ROOT/'work/counter_texture17')
    parser.add_argument('--self-test',action='store_true',help='Validate inputs, metadata and decoder guards without encoding or file writes')
    args = parser.parse_args()
    source = args.source.resolve(strict=True); output = args.output.resolve()
    require(source == ROOT/'app/obb/assets/bin/Data' or source.is_relative_to(ROOT/'work'), 'Unexpected source location')
    require(output.is_relative_to(ROOT/'work') and output != ROOT/'work' and (args.self_test or not output.exists())
            and not output.is_relative_to(source) and not source.is_relative_to(output), 'Output must be a new independent work directory')
    for p in (args.output.absolute(), *args.output.absolute().parents):
        if p.exists():
            require(not p.is_symlink() and not p.is_junction(), 'Output has a reparse ancestor')
        if p == ROOT:
            break
    for name,expected in RECEIPTS.items():
        require(sha(AUDIT/name) == expected, 'Reviewed analysis receipt changed: '+name)
    require(sha(ROOT/'scripts/convert_textures.py') == HELPER_SHA, 'Existing encoder helper changed')
    for name,expected in GUARDS.items():
        require(sha(source/name) == expected, 'Source preimage changed: '+name)
    require(sha(PC/STREAM) == PC_STREAM_SHA, 'Immutable PC stream preimage changed')
    selection = json.loads((AUDIT/'cafe-bounded-selection.json').read_text())
    append = json.loads((AUDIT/'cafe-encoder-append-plan.json').read_text())['appendOnlyPlan']
    plans = append['textureObjects']
    require(tuple(r['pathId'] for r in plans) == TARGETS, 'Reviewed append order changed')
    protected = {(r['file'],r['pathId']) for r in json.loads((AUDIT/'protected-texture-references.json').read_text())['protected']}
    require(not any((ASSET,pid) in protected for pid in TARGETS), 'Target is protected UI/font/script texture')
    raw = (source/ASSET).read_bytes()
    self_checks(raw,plans)
    if args.self_test:
        print('COUNTER_TEXTURE_SELFTEST_PASS: exact inputs, metadata roundtrip, unrelated objects, alpha/normal negative guards; no files written')
        return
    tops, quality, contexts = {}, [], {}
    for pid in TARGETS:
        row = next(r for r in selection['reviewedMaps'] if r['pathId'] == pid)
        descriptor = row['singleMipEncodeTextureInput']; mip = row['missing2048Mip']
        require(descriptor['m_MipCount'] == 1 and descriptor['m_ImageCount'] == 1
                and descriptor['m_TextureFormat'] == (10 if pid == 1186 else 12)
                and descriptor['target_format'] == 56, 'Unreviewed encoder contract')
        require(range_sha(source/STREAM,row['currentStream']['offset'],row['currentStream']['size']) == row['currentTailSha256'],
                'Current encoded tail changed')
        with (PC/STREAM).open('rb') as f:
            f.seek(mip['sourceMipOffset']); original_raw = f.read(mip['sourceMipBytes'])
        require(digest(original_raw) == row['originalMipSha256'], 'Original top mip changed')
        print('ENCODE_ORIGINAL_MIP',pid,descriptor['m_Name'],flush=True)
        encoded, helper_metrics = encode_texture(descriptor,original_raw,None,contexts)
        require(len(encoded) == 1871424, 'Unexpected ASTC6 top mip length')
        original = np.asarray(parse_image_data(original_raw,2048,2048,descriptor['m_TextureFormat'],
                              (2020,3,30,1),BuildTarget.StandaloneWindows64,flip=False).convert('RGBA'))
        decoded = np.frombuffer(texture2ddecoder.decode_astc(encoded,2048,2048,6,6),np.uint8).reshape(2048,2048,4)[:,:,[2,1,0,3]]
        metrics = quality_metrics(pid,original,decoded)
        require(np.allclose(metrics['mseRGBA'],helper_metrics['top_mip_mse_rgba'],rtol=1e-12,atol=1e-12), 'Independent decoder metrics disagree')
        tops[pid] = encoded
        quality.append(dict(pathId=pid,originalMipSha256=row['originalMipSha256'],encodedTopMipSha256=digest(encoded),
                            unchangedTailSha256=row['currentTailSha256'],**metrics))
        print('DECODE_QUALITY_PASS',pid,json.dumps(metrics),flush=True)
    metadata, changes, unchanged = patch_metadata(raw,plans)
    output.mkdir(parents=True,exist_ok=False)
    (output/'objects').mkdir()
    with (source/STREAM).open('rb') as original, (output/STREAM).open('xb') as target:
        require(copy_range(original,target,append['sourceStreamBytes']) == GUARDS[STREAM], 'Copied old prefix differs')
        for row in plans:
            require(target.tell() == row['newOffset'], 'Append offset differs')
            target.write(tops[row['pathId']])
            original.seek(row['oldStream']['offset'])
            tail_hash = copy_range(original,target,row['oldStream']['size'])
            require(tail_hash == next(r for r in quality if r['pathId']==row['pathId'])['unchangedTailSha256'], 'Copied lower tail differs')
        require(target.tell() == append['newStreamBytes'], 'Output stream size differs')
    (output/ASSET).write_bytes(metadata)
    require(range_sha(output/STREAM,0,append['sourceStreamBytes']) == GUARDS[STREAM], 'Persisted original prefix differs')
    check = UnityPy.load((output/ASSET).read_bytes())
    for row in plans:
        obj = check.file.objects[row['pathId']]; tree = obj.read_typetree(); q = next(r for r in quality if r['pathId']==row['pathId'])
        require(range_sha(output/STREAM,row['newOffset'],row['prefixBytes']) == q['encodedTopMipSha256'], 'Persisted top mip differs')
        require(range_sha(output/STREAM,row['newOffset']+row['prefixBytes'],row['unchangedTailBytes']) == q['unchangedTailSha256'], 'Persisted lower mips differ')
        require(tree['m_StreamData'] == {'path':STREAM+'.resource','offset':row['newOffset'],'size':row['newSize']}, 'Persisted native alias/range differs')
        (output/'objects'/f'{ASSET}.{row["pathId"]}.Texture2D').write_bytes(obj.get_raw_data())
    require((output/ASSET).read_bytes() == metadata, 'Persisted metadata differs')
    for name,expected in GUARDS.items():
        require(sha(source/name) == expected, 'Source changed concurrently: '+name)
    require(sha(PC/STREAM) == PC_STREAM_SHA, 'Original PC stream changed')
    report = dict(profile='counter_original_2k_append_only',source=str(source),originalPC=str(PC),sourceHashes=GUARDS,
        originalPCStreamSha256=PC_STREAM_SHA,encoderHelperSha256=HELPER_SHA,reviewedReceiptHashes=RECEIPTS,
        encoderPackages={p:importlib.metadata.version(p) for p in ('astc-encoder-py','texture2ddecoder','numpy')},
        encoderPolicy='Existing encode_texture ASTC6x6 MEDIUM,4threads; albedo LDR_SRGB, normal LDR; authored DXT5 AG unchanged',
        selectedTextureIds=list(TARGETS),changedObjects=changes,unchangedRawObjectsVerified=unchanged,
        quality=quality,appendOnly=append,outputFiles={name:dict(bytes=(output/name).stat().st_size,sha256=sha(output/name)) for name in (ASSET,STREAM)},
        allOriginalStreamBytesPreserved=True,allOtherOffsetsPreserved=True,allUIFontsAndUnrelatedObjectsPreserved=True,
        originalAndCanonicalInputsUnchanged=True,deviceOrCanonicalModified=False,
        shortcut='Retain1,252,576bytes of orphaned old1Ktails to avoid remapping all other consumers; active mip growth3,742,848bytes; file growth4,995,424bytes.',
        runtimeStatus='CANDIDATE_ONLY; actual indoor appearance, loaded mip, memory and warm frametime still require parent validation')
    (output/'manifest.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print('COUNTER_TEXTURE_CANDIDATE_READY',str(output),flush=True)


if __name__ == '__main__':
    main()
