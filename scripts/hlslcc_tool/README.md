# Local DXBC to GLSL ES 3.10 tool

Uses the already authorized local Unity-Technologies HLSLcc revision `3ea1fcd6` in `work/tools/HLSLcc`. Builds a static Windows CLI using installed MSVC/Windows SDK; no dependency fetch. HLSLcc source is unchanged.

```powershell
& '.\scripts\hlslcc_tool\build.ps1'
& '.\work\hlslcc_build\hlslcc_tool.exe' input.dxbc output.glsl reflection.json
```

The input must be a complete DXBC container beginning at `DXBC`, without Unity's program-code prefix/tail. The CLI validates container/chunk extents. `extract_fixture.py` extracts the first four DXBC programs of a named D3D11 Unity 2020 Shader using `<offset,size,segment>` entries and all compressed segments; it is a fixture helper, not a full shader repacker.

```powershell
& 'C:\Program Files\Python313\python.exe' scripts\hlslcc_tool\extract_fixture.py 'external/game/TheClosingShift_Data\sharedassets0.assets' 'Hidden/postVHSPro_First' work\hlslcc_build\vhs
```

Stripped Unity bytecode often contains only ISGN/OSGN/SHDR. HLSLcc's original missing-RDEF path leaves constant-buffer/resource reflection empty; translation then indexes those missing records. The wrapper reconstructs register-only RDEF for SM4.0/5.0 constant buffers, regular textures and samplers. Those diagnostic outputs use `cbN_data`, `tN` and `sN`, and explicitly report `reflectionMode: registerOnly`. They are not suitable Unity game shaders until real semantics are supplied. Register numbers differ between vertex and fragment stages, so identical placeholder names must not be blindly linked across stages. Structured/raw/UAV register-only reconstruction is rejected.

For recovered Unity shader-tail parameters, use semantic JSON:

```json
{
  "constantBuffers": [
    {
      "name": "UnityPerDraw",
      "binding": 0,
      "size": 64,
      "variables": [
        {
          "name": "unity_ObjectToWorld",
          "offset": 0,
          "size": 64,
          "class": "matrix_columns",
          "type": "float",
          "rows": 4,
          "columns": 4,
          "elements": 0
        }
      ]
    }
  ],
  "resources": [
    { "name": "_MainTex", "kind": "texture", "binding": 0, "dimension": "texture2d", "returnType": "float" },
    { "name": "sampler_MainTex", "kind": "sampler", "binding": 0 }
  ]
}
```

```powershell
& 'C:\Program Files\Python313\python.exe' scripts\hlslcc_tool\convert_with_metadata.py input.dxbc semantic.json output.glsl reflection.json
```

The Python helper only uses the standard library. It exposes `build_rdef(metadata, major=5)` for the shader-tail parser and writes a reviewable `.rdef` beside the reflection JSON. Native CLI also accepts an external RDEF payload directly as its fourth argument:

```text
hlslcc_tool input.dxbc output.glsl reflection.json semantic.rdef
```

All names, register bindings, byte offsets, explicit variable sizes, matrix orientation and shapes must come from actual metadata. The builder checks extents, shapes, overlap, duplicate slots/names and rejects unsupported types/kinds. It does not invent semantic names or fill unknown gaps. External RDEF replaces any original RDEF only inside the temporary in-memory container used by HLSLcc; checksum bytes are cleared because this container is not an executable D3D artifact.

Flags select mobile target, combined texture/samplers, semantic input/output names, flat uniforms, translated matrices and disabled explicit attribute locations. `DISABLE_GLOBALS_STRUCT` is necessary for matching flat-uniform declarations/references in this HLSLcc revision. `TRANSLATE_MATRICES` produces names such as `hlslcc_mtx4x4unity_ObjectToWorld`. JSON includes mode, GL stage, flags, callbacks, complete constant-buffer variable layout, resource register bindings and combined sampler names.

Validation completed: reflected/stripped/external-RDEF native self-tests, Python semantic-layout checks and invalid-extent rejection, original Standard vertex program, and all four original VHS programs including filmgrain/fisheye. Nine final GLSL outputs pass the installed NDK glslc parser with `--target-env=opengl -fauto-map-locations -fauto-bind-uniforms`. This validates shader syntax; Unity parameter bindings, linked programs, framebuffer orientation and device rendering remain separate tests.

Structured Unity instancing constant buffers use `hlslcc_tool_ubo.exe` with a final `--ubo` argument. This adds uniform-buffer mode while keeping `$Globals` as flat uniforms. Blocks retain their actual Unity names, member names and `UNITY_RUNTIME_INSTANCING_ARRAY_SIZE`. The Python helper selects this executable automatically when semantic JSON has `requiresUniformBufferObjects: true`. The original flat executable remains available for ordinary variants and existing hash-keyed caches.

```text
hlslcc_tool_ubo input.dxbc output.glsl reflection.json semantic.rdef --ubo
```

All five distinct structured layouts in the original Standard shader pass semantic-RDEF reconstruction, native HLSLcc translation and ES 3.1 syntax validation. A complete Standard VS816/PS2832 pair also passes stage validation and Unity GLES record/tail round-trip checks. Evidence is under `work/evidence/shader_format/instancing_fixture/`. Actual Unity device draws remain a separate acceptance check.
