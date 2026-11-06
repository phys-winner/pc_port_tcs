# Release unused CPU backing for Aura shadow texture arrays

This work-only patch keeps the previously guarded RFloat shadow formats and changes one original Aura method: Texture2DArrayComposer.Generate, token06000120. Immediately after a fresh Texture2DArray is constructed and stored, Android/OpenGLES3 calls Apply(false,true), then the existing Graphics.CopyTexture initialization/update loop continues. The block is never re-entered for ordinary copies to an existing array. No shadow resolution, float precision, mip, texture contents, binding or event is changed.

The constructor has no mip chain. All initialization and later updates use GPU Graphics.CopyTexture. The patcher scans all113 current managed assemblies, and rejects any non-engine Texture2DArray CPU reader/writer or pre-existing Apply. It records all input assembly hashes and direct call evidence in the output manifest. The apparent ES3Type_Texture2DArray actually serializes Texture2D[]; the private Aura runtime arrays have no serialized save root. Texture3DAtlas CPU GetPixels/SetPixels is a separate path and remains untouched.

Unity2020.3 documents that [Texture2DArray.Apply makeNoLongerReadable](https://docs.unity3d.com/cn/2020.3/ScriptReference/Texture2DArray.Apply.html) removes the CPU copy. That call must precede GPU copies because Apply can replace GPU contents with CPU data. [Graphics.CopyTexture](https://docs.unity3d.com/2020.3/Documentation/ScriptReference/Graphics.CopyTexture.html) continues to perform GPU copies; its optional CPU-side copy requires both textures to remain readable. This patch intentionally never calls Apply after those GPU updates.

Reproduce from the frozen RFloat candidate:

```powershell
./scripts/patch_aura_array_readability/build.ps1
```

Default source: work/android_aura_shadow_memory/managed/Aura2_Core.dll, SHA256f9fb15c6ee4e0956ce3be176828c82f23d05693bc4700401f1b44582b72b688f. Default output: work/android_aura_array_nocpu/managed/Aura2_Core.dll, SHA256de954e69e912fb252c23a5b10a5cea13559664aeb76994d3f574b976ed56d73d. Its sibling Aura2_Core.dll.manifest.json records the checks.

Strict guards: exact preimage hash and method body; sole constructor/setter/copy-loop shape; eleven inserted instructions; Android11/GLES3=11; exact no-mip argument; all1112 original tokens and468 bodies; unchanged assembly identity/dependencies; unchanged PE table counts and per-type method ownership; MethodPtr0 and no added definitions. Readback must equal the intended body, then removing precisely the inserted block must restore the original body. Two builds were byte-identical. Output stays in work; the patcher does not apply it to app or device.

For the authored initial15 spot and16 point shadow layers, the retained CPU payload after the previous RFloat change is3.75MiB+32MiB=35.75MiB. Releasing that backing is a theoretical payload saving, not a measured process PSS claim. The initial constructor still needs a one-time CPU allocation. Subsequent run0019 reached a separate, now precisely captured null sampler-binding write during compute resource resolution; that failure does not prove this backing-release patch failed or establish its total RAM effect.
