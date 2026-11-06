# Exact Unity 2020.3.30f1 ARMv7 OBB IO compatibility

This source adapts only two `libunity.so` PLT relocations, `fseek` and `ftell`, to 64-bit libc file IO while preserving the native ZIP32 long32 bit pattern. It does not modify the Unity binary on disk, translate CPUs, unpack caches, alter licensing or request storage permissions. The library is built only into `work/build/large_obb_io`; canonical `app/` and the device are owned by the coordinator.

Native proof is in `work/evidence/zip-entry-disassembly.txt`, `native-file-annotated.txt`, and `zip-directory-entry-disassembly.txt`. `ZipFile` stores its base payload offset with `str r0,[this,#0xc]` at 0x4454da. STORED seeks add base and logical offset with 32-bit arithmetic at 0x445980 and call GenericFile::seek(long32,int). Its tell and read operations subtract base in 32-bit registers; no 64-bit sign extension occurs. `NativeFile::seek` reaches the libc `fseek` PLT slot, and its tell reaches `ftell`.

The compatibility contract is bounded to ZIP32 archives below 4 GiB and individual physical entries below 2 GiB. Split larger Unity streams into uniform `.split0`, `.split1`, etc. chunks, with the final chunk allowed shorter. Native seek receives absolute ZIP32 offsets as unsigned32 when origin is SEEK_SET; SEEK_CUR/SEEK_END offsets retain signed32 meaning. Tell returns the exact low32 bit pattern through the long32 ABI, or -1/EOVERFLOW above UINT32_MAX. Actual 64-bit libc functions are resolved with dlsym, so missing platform support rejects initialization clearly.

Build:

```powershell
& 'C:\Program Files\Python313\python.exe' -B scripts/large_obb_io/build.py
```

The build pins NDK 27.2.12479018 and the official Mono ARMv7 Release Unity library SHA-256. Runtime checks the exact GNU build ID, five 16-byte machine-code preimages, ELF machine/class, bounded ELF structures, the exact two JUMP_SLOT offsets and their original libc pointer identities. It locates the image with dl_iterate_phdr, changes the necessary non-executable GOT page protection briefly, stores two aligned pointers and restores the prior page protection. The independent GLOB_DAT pointer for fseek is deliberately untouched; the verified NativeFile implementation uses the PLT route.

The coordinator should copy the resulting library to `app/lib/armeabi-v7a/liblargeobbio.so` and Java helper to the corresponding package under `app/src`. In the launch Activity, call the helper **before** the call to the base UnityPlayerActivity.onCreate, which constructs UnityPlayer:

```java
int ioResult = com.chillasart.theclosingshift.port.LargeObbIO.install(
        getCacheDir().getAbsolutePath());
if (ioResult != 0) {
    throw new IllegalStateException("Large OBB IO initialization failed: " + ioResult);
}
super.onCreate(savedInstanceState);
```

The helper loads the compatibility library. If Unity's native ELF is not mapped yet, the compatibility library uses `dlopen("libunity.so", RTLD_NOW|RTLD_LOCAL)` and keeps that handle. It does not invoke Unity's JNI_OnLoad; the ordinary UnityPlayer NativeLoader continues its normal load/initialization sequence afterward. No separate early System.loadLibrary("unity") is needed. This integration and the exact NativeLoader sequence still require the coordinator's device run.

Before returning success, installation creates an unlinked sparse temporary file in the supplied app cache directory. It writes two 16-byte markers across the 2-GiB boundary and at 3.625 GiB+123, reads through function pointers loaded from the patched Unity GOT slots, checks both tell bit patterns, negative relative/end seeks, and EOVERFLOW beyond 4 GiB. The file has no persistent directory entry and allocates only sparse marker blocks. A successful run logs `sparse app-UID probe PASS at 2 GiB and 3.625 GiB`. The probe is meaningful file IO evidence, but actual Unity serialized/resS reads remain a separate required game test.

Only three success messages are possible per process (first large seek, first large tell, installed/probe PASS); a failed installation logs one error. Native install returns zero only after the fixture passes. Abort initialization on a nonzero result. Repeated successful calls are idempotent. There is no uninstall/repatch path during a live Unity process.

Coordinator runtime evidence now exists: `work/evidence/run0002-large-obb-io/startup.log`, lines 33–35, records the sparse app-UID fixture PASS, tell position 2147483656, and absolute seek 3892314235. The evidence identity/markers are preserved separately in `work/build/large_obb_io/runtime-verification.json`, leaving the build-time NOT RUN field historically accurate. The subsequent game run reaches managed gameplay and postprocessing shader diagnostics; it no longer exhibits the earlier serialized empty/corrupt failures in that captured log. Full scene and streamed-resource correctness remains a separate verification task.
