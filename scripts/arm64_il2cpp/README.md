# ARM64 IL2CPP pipeline

The current entry point is `build_release.ps1`. It consumes the locally prepared
canonical `app/` layout and the matching Unity 2020.3.30f1 Android toolchain. See
[BUILDING.md](../../BUILDING.md) for the complete prerequisites and reproduction
boundary.

```powershell
& scripts/arm64_il2cpp/build_release.ps1 -BuildDirectory work/a64-release-next
```

The script performs frozen preparation, IL2CPP conversion, native compilation,
staging and compressed single-APK packaging. Every build uses a new directory;
receipts in previous directories are immutable. The final APK is
`work/a64-release-next/package/base.apk`.

`actual_build.py` exposes the individual `--prepare --controls-frozen`,
`--convert`, `--compile` and `--stage-app` stages. It verifies exact input hashes,
AArch64 native libraries, metadata/cache identity and the staged layout. Its
legacy `--build-obb` path is not the current single-APK workflow.

`patch_aot/` and `AotReflectionFallbacks.cs` adapt selected Newtonsoft/Odin runtime
reflection paths for AOT. `build_aot_compat.ps1 -ToolsOnly` compiles the build tools
and helper; without that switch it additionally tests work copies of the original
PC assemblies. No runtime JIT is introduced.

`-QaOrders` selects the hidden QA runtime variant. `-PerfProbe` is for diagnostics
only and must not be used in a published package. Changing runtime helpers requires
a fresh IL2CPP/native build; old metadata cannot be mixed with a new library.

`verify_device.py` verifies a selected staged build against an explicitly chosen
Android device. It requires local build receipts and Android tooling. Host checks
alone do not establish gameplay or hardware acceptance.
