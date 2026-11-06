# Building pc_port_tcs

## Reproduction boundary

This repository provides the port runtime, patchers, authored translation and UI
routing data, generators, Android launcher sources and source fixtures. It does
not contain the original game, a Unity project or a complete automated pipeline
from a fresh Steam installation to the current prepared Android assets.

A fresh clone can run the source fixtures and regenerate the port's UI tables.
The native/APK build additionally needs a prepared canonical `app/` layout from
the porting workflow. Several historical asset transformations depend on exact
intermediate preimages, source inventories or shader fixtures. Those contain
game material and are deliberately excluded from Git. Owning the game alone does
not reconstruct these intermediate revisions automatically.

The supported game product is
[The Closing Shift, Steam App ID 1843090](https://store.steampowered.com/app/1843090/).
Patcher hashes and structural checks define the revisions each tool accepts;
compatibility with every past or future Steam update is not implied. Never relax
these guards to force a different revision through the pipeline.

## Build host and tools

The native build runs on Windows x64. Run commands from the repository root; the
folder may be named `pc_port_tcs` because the main pipeline derives its root from
its own location.

- **Python 3.13**. Several PowerShell scripts invoke
  `C:/Program Files/Python313/python.exe` directly; adjust that local tool path if
  needed. The packaging self-test and UI table generators use the standard
  library. Asset tools also need the packages in `requirements.txt`.
- **.NET SDK 10**, including Roslyn, for `net10.0` patcher and fixture projects.
- **Unity 2020.3.30f1 with Android IL2CPP**, provisioned at
  `work/tools/Unity2020/Editor/`. The engine DLLs, AOT BCL, Baselib and Android
  native libraries must come from that matching installation.
- **Android NDK 19.0.5232133**, supplied by that Unity AndroidPlayer, for the main
  IL2CPP build. It is intentionally distinct from newer shader-tool NDKs.
- **Android SDK**, a local **JDK**, build-tools, AAPT2, D8, zipalign and apksigner.
  `build_release.ps1` defaults to
  `$env:LOCALAPPDATA/Android/Sdk/platforms/android-37.0/android.jar`; pass
  `-AndroidJar` to use the correct installed platform explicitly. Older standalone
  checks/packagers use Android 32 or 36 paths and need their stated platforms.
- **dnlib** for the managed patchers. Existing project files use
  `C:/Portable/Total.Commander/Utilites/dnSpy/bin/dnlib.dll` as a tool default;
  provide dnlib there or adjust `HintPath` for your installation.
  `patch_managed.csproj` also accepts `-p:DnlibPath=...`.

Optional shader/texture stages additionally use NDK **27.2.12479018**, CMake
**3.22.1**, Ninja, Visual Studio, external HLSLcc sources under `work/tools/HLSLcc/`,
matching Unity built-in shaders and glslangValidator/glslc. Their exact paths and
input checks are specified in the individual source scripts. A build from the
prepared canonical layout does not rerun every historical converter.

Install the Python dependencies in a suitable local environment:

```powershell
python -m venv .venv
.venv/Scripts/python.exe -m pip install -r requirements.txt
```

Scripts that call the fixed Python path still use that interpreter; install the
asset dependencies there or update the path consistently. `requirements.txt`
records the direct package versions from the development environment, rather
than a fully verified dependency lock for a fresh installation.

## Source checks without the game

These commands do not need game assets or a Unity installation:

```powershell
python -X utf8 -B scripts/build_apk.py --self-test
python -X utf8 -B scripts/russian_localization/generate_ui_data.py
python -X utf8 -B scripts/patch_managed/generate_ui_routes.py
$fixtures = @(
    'scripts/patch_managed/touch_fixture/touch_fixture.csproj',
    'scripts/patch_managed/graphics_fixture/graphics_fixture.csproj',
    'scripts/patch_managed/ao_fixture/ao_fixture.csproj',
    'scripts/patch_managed/color_fixture/color_fixture.csproj',
    'scripts/patch_managed/culling_fixture/culling_fixture.csproj',
    'scripts/patch_managed/lifecycle_fixture/lifecycle_fixture.csproj',
    'scripts/patch_managed/qa_fixture/qa_fixture.csproj',
    'scripts/patch_managed/volume_fixture/volume_fixture.csproj',
    'scripts/mobile_render/render_fixture/render_fixture.csproj',
    'scripts/russian_localization/policy_test/policy_test.csproj'
)
foreach ($fixture in $fixtures) {
    dotnet run --project $fixture -c Release
    if ($LASTEXITCODE -ne 0) { throw "Fixture failed: $fixture" }
}
```

Generate the Russian UI table before running the locale policy fixture to include
its generated-table assertions. The generators consume the compact authored data
in `scripts/russian_localization/data/` and `scripts/patch_managed/ui_routes.json`.
Their outputs and provenance reports are ignored by Git.

The fixtures use synthetic Unity API stubs and synthetic scene geometry. They
check port policies and behavior, not actual rendering, game startup, campaign
progression or device performance.

## Private inputs for the full pipeline

| Local path | Required content |
| --- | --- |
| `external/game/TheClosingShift_Data/` | Your own original PC game data and `Managed/` assemblies; default for tools that read the PC source |
| `app/assets/bin/Data/` | Prepared bootstrap assets, `boot.config`, scripting metadata and the accepted managed input set |
| `app/assets/bin/Data/Managed/` | Prepared game and engine assemblies plus the port helpers required by `actual_build.py` |
| `app/obb/assets/bin/Data/` | Prepared bulk scenes, resources, shaders, streams and videos; merged into the single APK during staging |
| `app/obb/assets/<unity.build-id>` | Empty build marker matching the manifest |
| `work/tools/Unity2020/Editor/` | Matching Unity Android toolchain |
| Exact `work/*_input` and retained revision paths used by a converter | Its reviewed game-data preimages and fixtures, when rerunning that converter |

Obtain the original data and external tools yourself. `external/`, game-bearing
parts of `app/`, `work/` and `pub/` are ignored. A local directory link to your own
installation can provide the `external/game/TheClosingShift_Data/` path without
copying the game. Use explicit `--source`, `--original-data` or `-Managed`
arguments where the selected script supports them.

The required canonical helper names are `ClosingShift.Android.dll`,
`ClosingShift.RussianLocale.dll`, `ClosingShift.RussianFonts.dll`,
`ClosingShift.MobileRender.dll` and `ClosingShift.AuraGles.dll`, alongside the
prepared game/engine assemblies. Their source changes must first be compiled and
integrated into the matching input set; the release script does not rebuild all
canonical helpers automatically.

`generate_room_occluders.py` bakes `AndroidRoomOccluders.cs` locally from the prepared
opaque wall meshes. The generated vertices are excluded from Git.
`patch_managed/build.ps1` regenerates both the UI routes and room occluders before
compiling the runtime. The authored locale inputs generate `RussianUiData.cs`
without requiring private localization inventories. `prepare_russian_i2.py`
reads the original language object directly from your PC `resources.assets`,
checks its exact hash and preserves the original language columns.

## APK signing configuration

`scripts/build_apk.py` reads the local machine configuration at
`~/.agents/skills/adapt-arm32-game/machine_config.json`. The relevant fields are:

```json
{
  "project_root": "C:/PortTools",
  "build_tools_version": "VERSION",
  "java_exe": "C:/JDK/bin/java.exe",
  "signer": {
    "dir": "C:/PortSigning",
    "apksigner": "apksigner.jar",
    "key": "release.pk8",
    "cert": "release.x509.pem",
    "expected_cert_sha256": "REPLACE_WITH_SHA256_OF_DER_CERTIFICATE"
  }
}
```

Build-tools are resolved under
`<project_root>/tools/android/sdk/build-tools/<build_tools_version>/`. The
packager verifies the DER certificate SHA-256 before signing. Supply your own
private key and certificate; keys and machine configuration are never committed.
APK signing is separate from signing a Git commit.

## Build from a prepared canonical layout

Check available disk space and preserve the exact canonical inputs first. Always
choose a **new** build directory; existing directories with receipts are rejected.

```powershell
& scripts/arm64_il2cpp/build_release.ps1 -BuildDirectory work/a64-release-next
```

The script builds host tools and the AOT helper, then runs
`--prepare --controls-frozen`, `--convert`, `--compile`, `--stage-app` and compressed
APK packaging. The result is:

```text
work/a64-release-next/package/base.apk
```

`-QaOrders` compiles the current Android runtime with the hidden QA controls and
requires a new native build. `-PerfProbe` is diagnostic-only, requires `-QaOrders`
and must not be used for a published package. A normal build uses the existing
canonical Android helper. Do not apply the full game/firstpass patcher repeatedly
on already patched assemblies.

The pipeline checks frozen input hashes, native/metadata identity and ELF
architecture/alignment. Device verification, save/load and gameplay checks remain
separate requirements. No full game build or installation was performed solely
for this source publication.
