# The Closing Shift: Android ARM64 port

Unofficial Android ARM64 porting tools and compatibility code for
**[Chilla's Art] The Closing Shift | 閉店事件**, compatible with the Windows game
sold on [Steam (App ID 1843090)](https://store.steampowered.com/app/1843090/).

**A legally obtained copy of the original game is required.** This repository
contains the port's source code, authored patch data and build tools. Game
executables, managed game assemblies, scenes, meshes, textures, audio, videos,
Unity binaries and ready-to-play packages are excluded.

Project: [phys-winner/pc_port_tcs](https://github.com/phys-winner/pc_port_tcs).
This is an independent project and is not affiliated with or endorsed by
Chilla's Art or Unity Technologies.

## Status

The current development version is **0.1-dev** (`versionCode=1`). The target is
**Android 6.0 or later**, **ARM64 / AArch64**, **OpenGL ES 3.1** and ASTC textures.
The Android package name is `com.chillasart.theclosingshift.port`.

The current local packaging path produces a **single APK**, with no external OBB.
Source fixtures, packaging integrity, native architecture and signing have been
checked during development. The exact 0.1-dev APK has not received a new device
acceptance run; earlier development builds were exercised on selected Adreno and
Mali devices. A complete campaign playthrough and broad hardware compatibility
have not been verified. Performance depends strongly on the device and settings.

| Reproduction task | Current support |
| --- | --- |
| Build and run standalone source fixtures | Supported without game files |
| Regenerate port UI tables | Supported from the authored data in this repository |
| Bake room occluders | Requires the locally prepared game scenes; extracted geometry stays outside Git |
| Compile IL2CPP and package a new APK | Requires the prepared canonical game layout and matching external toolchains |
| Turn an untouched Steam installation into that canonical layout in one command | Not yet supported |

The repository is a source release of the adaptation, not a self-contained Unity
project. [BUILDING.md](BUILDING.md) lists the required inputs, commands and current
reproduction limits. Do not bypass exact input hashes to accommodate another game
revision.

## Port features

- ARM64 IL2CPP compilation, AOT reflection fallbacks and checks tying native code,
  metadata and Unity's extracted-data cache to the same build.
- Touch movement and camera control, context-sensitive interaction with hold
  support, inventory navigation, dialogue selection and a three-finger crouch
  gesture. Gamepad support is retained.
- Full-width landscape presentation, display cutout handling, configurable touch
  sensitivity, left-handed controls and interface scaling.
- Independent 3D render scale, frame-rate selection and mobile graphics policies,
  including guarded adaptations for lighting, ambient occlusion, video and VHS
  effects. Conservative room culling preserves shadow and camera behavior.
- App-specific save storage, startup error handling, lifecycle fixes and rotating
  session logs. No broad external-storage permission is requested.
- Russian localization with Cyrillic font support, contextual UI captions and
  visible attribution to **ChatGPT (OpenAI)**. Manual language selection is kept.

## Source layout

| Path | Purpose |
| --- | --- |
| `app/AndroidManifest.xml`, `app/src/`, `app/res/` | Android launcher, Java bridges, manifest and XML resources |
| `app/assets/port-defaults/` | Authored default key bindings |
| `scripts/arm64_il2cpp/` | IL2CPP stages, AOT patches and native/device verification |
| `scripts/patch_managed/` | Managed patcher, Android runtime and isolated fixtures |
| `scripts/android/` | Android checks and diagnostic tools |
| `scripts/mobile_render/` | Render-scale and load policies |
| `scripts/russian_fonts/`, `scripts/russian_localization/` | Font compatibility, authored translations and locale generation |
| `scripts/hlslcc_tool/`, `scripts/compute_hlslcc/` | Wrappers for external shader translation tools |
| `scripts/prepare_*.py`, `scripts/apply_*.py` | Guarded asset transformations and compatibility fixes |
| `scripts/build_apk.py`, `scripts/build_obb.py` | APK packaging and shared/legacy OBB routines |

The allowlist in [.gitignore](.gitignore) excludes local game data, generated
geometry and tables, SDKs, build outputs, signing keys, saves, logs and internal
work notes. Original-language strings used as patch keys and scene identifiers
are compatibility metadata, not a bundled copy of the game's resources.

## Quick source checks

Use Python 3.13 and .NET SDK 10 from the repository root:

```powershell
python -X utf8 -B scripts/build_apk.py --self-test
python -X utf8 -B scripts/russian_localization/generate_ui_data.py
python -X utf8 -B scripts/patch_managed/generate_ui_routes.py
dotnet run --project scripts/patch_managed/touch_fixture/touch_fixture.csproj -c Release
dotnet run --project scripts/patch_managed/graphics_fixture/graphics_fixture.csproj -c Release
dotnet run --project scripts/russian_localization/policy_test/policy_test.csproj -c Release
```

See [BUILDING.md](BUILDING.md) for the complete fixture list and the prepared-input
APK build. These checks use Unity API stubs; they do not run the game.

## License and credits

Copyright (C) 2026 Eugene.

Original code and authored contributions to this port are licensed under the
**GNU General Public License, version 3 or (at your option) any later version**
(`GPL-3.0-or-later`). See [LICENSE](LICENSE). GPL is a strong copyleft license:
redistributing a covered modified work requires providing its corresponding
source under the applicable GPL terms.

The original game and its assets belong to **Chilla's Art**. Unity, commercial
middleware and other third-party components retain their own licenses. The GPL
grant covers the port's original contributions; it does not relicense those
components or grant permission to redistribute the combined game package.
[THIRD_PARTY.md](THIRD_PARTY.md) documents the dependency and template boundaries.
