# Third-party components and provenance

The GPL-3.0-or-later grant in [README.md](README.md) and [LICENSE](LICENSE) applies
to the port's original code and authored contributions. Existing third-party
material retains its original ownership and terms. This repository does not
provide a license to the game, Unity or commercial middleware, and it does not
authorize redistribution of a combined APK containing them.

| Component | Role | Repository contents |
| --- | --- | --- |
| [The Closing Shift / Chilla's Art](https://store.steampowered.com/app/1843090/) | Original PC game, managed assemblies, scenes, textures, audio, video and middleware | No original game files; only compatibility identifiers, hashes and authored patch/localization data |
| [Unity 2020.3](https://unity.com/releases/editor/archive) | Android engine, IL2CPP, AOT BCL, Baselib and player classes | No engine binaries or SDK; the Android activity and theme resources include Unity template material with port-authored modifications |
| [Unity built-in shaders](https://unity.com/releases/editor/archive) | Exact inputs for selected shader adaptations | Original shader packages are external; port transformation scripts are included |
| [Android SDK/NDK](https://developer.android.com/studio) and JDK | Java/native compilation and APK packaging | External tools only |
| [.NET SDK](https://dotnet.microsoft.com/download) and Visual Studio | Managed/native host tooling | External tools only |
| [dnlib](https://github.com/0xd4d/dnlib) | Managed assembly inspection and patching | External library; patcher sources and reference paths are included |
| [HLSLcc](https://github.com/Unity-Technologies/HLSLcc) | Shader translation | External dependency; this repository includes the port's wrappers and conversion/validation scripts |
| Packages in [requirements.txt](requirements.txt) | Unity asset parsing, texture processing, ELF inspection and compression | Dependency names/versions only |
| [FFmpeg](https://ffmpeg.org/) and ILSpy command-line tooling | Selected local media/analysis stages | External tools only |

`app/src/com/unity3d/player/UnityPlayerActivity.java` is based on Unity's Android
player activity template. The Unity template portions are not claimed as original
GPL code. The launcher themes in `app/res/values*/styles.xml` likewise contain
Unity Android template definitions. The port adds input, startup, storage,
display and lifecycle behavior around that integration. See Unity's documentation
on [extending UnityPlayerActivity](https://docs.unity3d.com/2020.3/Documentation/Manual/AndroidUnityPlayerActivity.html)
and obtain the matching player components under Unity's own terms.

The compact JSON files under `scripts/russian_localization/data/` contain authored
Russian translations, matching strings and scene-owner patch metadata. They omit
the original complete language database and raw extracted inventories. Original
names and matching identifiers retain their original ownership. The generated
Russian table and scene-derived room geometry are local outputs rather than
repository inputs.

The Russian translation is attributed in-game to **ChatGPT (OpenAI)**. The game
itself and its original credits remain the work of Chilla's Art and the original
credited contributors.

SDK archives, installers, Unity/third-party libraries, signing keys, user profiles,
saves and diagnostic captures are not part of this source release.
