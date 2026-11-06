"""Build the exact ARMv7 Unity ZIP32 seek/tell compatibility shim, without app writes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

UNITY_SHA256 = "1b844f6bd1e9ecf38af7d73276db7337ec45e3d10822acbca35046d6c160c3fc"
NDK_VERSION = "27.2.12479018"
NDK_DEFAULT = Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/ndk/27.2.12479018'
ROOT = Path(__file__).resolve().parents[2]
UNITY_DEFAULT = ROOT / "work/tools/Unity2020/Editor/Data/PlaybackEngines/AndroidPlayer/Variations/mono/Release/Libs/armeabi-v7a/libunity.so"


def sha256(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--ndk", type=Path, default=NDK_DEFAULT)
    parser.add_argument("--unity", type=Path, default=UNITY_DEFAULT)
    parser.add_argument("--out", type=Path, default=ROOT / "work/build/large_obb_io")
    args = parser.parse_args()
    if sha256(args.unity) != UNITY_SHA256:
        raise SystemExit("Unity native source hash mismatch; no shim built")
    properties = (args.ndk / "source.properties").read_text()
    if f"Pkg.Revision = {NDK_VERSION}" not in properties:
        raise SystemExit("Pinned NDK revision mismatch")
    from elftools.elf.elffile import ELFFile
    with args.unity.open("rb") as stream:
        elf = ELFFile(stream)
        notes = [note for segment in elf.iter_segments() if segment["p_type"] == "PT_NOTE"
                 for note in segment.iter_notes()]
        build_id = bytes.fromhex(next(n["n_desc"] for n in notes if n["n_type"] == "NT_GNU_BUILD_ID"))
        rel = elf.get_section_by_name(".rel.plt")
        dynsym = elf.get_section(rel["sh_link"])
        slots = {dynsym.get_symbol(r["r_info_sym"]).name: r["r_offset"]
                 for r in rel.iter_relocations()
                 if dynsym.get_symbol(r["r_info_sym"]).name in ("fseek", "ftell")}
        assert slots == {"fseek": 0x105C578, "ftell": 0x105C57C}, slots
        guards = []
        for address in (0x429B04, 0x445450, 0x4454CC, 0x4457EE, 0x445978):
            segment = next(s for s in elf.iter_segments() if s["p_type"] == "PT_LOAD"
                           and s["p_vaddr"] <= address < s["p_vaddr"] + s["p_filesz"])
            stream.seek(segment["p_offset"] + address - segment["p_vaddr"])
            guards.append((address, stream.read(16)))
    args.out.mkdir(parents=True, exist_ok=True)
    array = lambda b: ",".join(f"0x{x:02x}" for x in b)
    header = [f'#define UNITY_SHA256 "{UNITY_SHA256}"',
              f"#define EXPECTED_FSEEK_SLOT 0x{slots['fseek']:x}u",
              f"#define EXPECTED_FTELL_SLOT 0x{slots['ftell']:x}u",
              f"static const unsigned char expected_build_id[] = {{{array(build_id)}}};",
              "struct CodeGuard { uint32_t offset; unsigned char bytes[16]; };",
              "static const struct CodeGuard code_guards[] = {"]
    header += [f"{{0x{address:x}u, {{{array(data)}}}}}," for address, data in guards]
    header += ["};", ""]
    (args.out / "unity_preimage.h").write_text("\n".join(header))
    toolchain = args.ndk / "toolchains/llvm/prebuilt/windows-x86_64/bin"
    clang = toolchain / "clang.exe"
    output = args.out / "liblargeobbio.so"
    source = Path(__file__).with_name("large_obb_io.c")
    command = [str(clang), "--target=armv7a-linux-androideabi23", "-mthumb", "-fPIC", "-shared",
               "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror", "-fvisibility=hidden",
               "-Wl,-z,relro,-z,now,-z,max-page-size=16384", "-Wl,--no-undefined",
               "-I", str(args.out), str(source), "-o", str(output), "-llog", "-ldl"]
    result = subprocess.run(command, capture_output=True, text=True)
    (args.out / "build.log").write_text(result.stdout + result.stderr)
    if result.returncode:
        raise SystemExit(result.stdout + result.stderr)
    manifest = {"ndkRevision": NDK_VERSION, "compilerSha256": sha256(clang),
                "unitySha256": UNITY_SHA256, "unityBuildId": build_id.hex(),
                "sourceSha256": sha256(source), "librarySha256": sha256(output),
                "slots": slots, "guards": [{"offset": address, "hex": data.hex(),
                  "sha256": hashlib.sha256(data).hexdigest()} for address, data in guards],
                "command": command, "runtimeProbe": "NOT RUN: coordinator owns device"}
    (args.out / "build.json").write_text(json.dumps(manifest, indent=2))
    print(json.dumps({"library": str(output), "sha256": manifest["librarySha256"]}))


if __name__ == "__main__":
    main()
