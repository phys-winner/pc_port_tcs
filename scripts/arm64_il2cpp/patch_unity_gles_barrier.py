"""Pinned Unity GLES indirect-command barrier candidate; no SDK/app writes.

patch(raw) is pure and reusable by a build stage. The CLI has fixed vendor input
and private work output. This repairs a proven barrier omission, not a measured
explanation for the Mali fence hang. See aura-gpu22/README.md for the call chain.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / 'work/tools/Unity2020/Editor/Data/PlaybackEngines/AndroidPlayer/Variations/il2cpp/Release/Libs/arm64-v8a/libunity.so'
OUTPUT = ROOT / 'work/unity_gles_barrier22/libunity.so'
MANIFEST = OUTPUT.with_name('manifest.json')
SOURCE_SHA256 = '05d7ea5ec2764d4f5550d9df6edaf318c27df5a095798546e538ca32a7b01af5'
PATCHED_SHA256 = '9ec1220234c128ab2078fa69e5e0ecd9226a57fe2cba7ed5eb67044d8c3c16ed'
SOURCE_SIZE = 24439568
FUNCTION_OFFSET = 0x75850c
FUNCTION_SIZE = 332
FUNCTION_SHA256 = 'caaadc7fcd5a255c55db70208e0956d488a374a8b1642b4bffbf6a844e50c491'
PATCHED_FUNCTION_SHA256 = 'dd24fc5f219912f547fd8142b5474ff3fb5edff61aba7f08a5a4be2fdc2ec867'
PATCH_OFFSET = 0x7585bc
OLD_WORD = 0x52870589  # mov w9, #0x382c
NEW_WORD = 0x52870d89  # mov w9, #0x386c
CHANGED_BYTE = PATCH_OFFSET + 1
DISPATCH_CALL_OFFSET = FUNCTION_OFFSET + 0x138
DISPATCH_TARGET = 0x762c58
SPEC_URL = 'https://registry.khronos.org/OpenGL/specs/es/3.2/es_spec_3.2.pdf#page=142'


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def elf_tables(raw: bytes) -> tuple[bytes, bytes, bytes]:
    """Capture the ELF, program, and section headers without altering mappings."""
    require(len(raw) >= 64 and raw[:7] == b'\x7fELF\x02\x01\x01',
            'Expected ELF64 little-endian version 1')
    h = struct.unpack_from('<16sHHIQQQIHHHHHH', raw)
    require(h[1:4] == (3, 183, 1) and h[8] == 64,
            'Expected AArch64 ET_DYN ELF header')
    require(h[9:11] == (56, 8) and h[11:13] == (64, 28),
            'Unexpected pinned ELF table dimensions')
    ph_end = h[5] + h[9] * h[10]
    sh_end = h[6] + h[11] * h[12]
    require(64 <= h[5] < ph_end <= len(raw) and 64 <= h[6] < sh_end <= len(raw),
            'ELF header tables exceed file bounds')
    ph = raw[h[5]:ph_end]
    loads = [p for p in struct.iter_unpack('<IIQQQQQQ', ph) if p[0] == 1]
    require(all(p[2] + p[5] <= len(raw) and p[5] <= p[6] for p in loads),
            'Invalid PT_LOAD bounds')
    owner = [p for p in loads if p[2] <= FUNCTION_OFFSET
             and FUNCTION_OFFSET + FUNCTION_SIZE <= p[2] + p[5]]
    require(len(owner) == 1 and owner[0][1] == 5 and owner[0][2] == owner[0][3],
            'Function must remain in the pinned executable PT_LOAD, file offset = VA')
    return raw[:64], ph, raw[h[6]:sh_end]


def patch(raw: bytes) -> bytes:
    """Add only GL_COMMAND_BARRIER_BIT to this exact indirect-dispatch mask."""
    require(isinstance(raw, bytes) and len(raw) == SOURCE_SIZE,
            'Expected pinned libunity.so bytes and size')
    tables = elf_tables(raw)
    require(struct.unpack_from('<I', raw, PATCH_OFFSET)[0] == OLD_WORD,
            'Unexpected barrier instruction; wrong input or already patched')
    function = raw[FUNCTION_OFFSET:FUNCTION_OFFSET + FUNCTION_SIZE]
    require(digest(function) == FUNCTION_SHA256,
            'Surrounding matched indirect-dispatch function changed')
    require(digest(raw) == SOURCE_SHA256, 'Unexpected full libunity.so preimage SHA-256')
    call = struct.unpack_from('<I', raw, DISPATCH_CALL_OFFSET)[0]
    require(call >> 26 == 0b100101, 'Expected direct BL to ApiGLES::DispatchIndirect')
    imm = call & 0x03ffffff
    signed_imm = imm if imm < 0x02000000 else imm - 0x04000000
    require(DISPATCH_CALL_OFFSET + signed_imm * 4 == DISPATCH_TARGET,
            'ApiGLES::DispatchIndirect call target changed')

    candidate = bytearray(raw)
    struct.pack_into('<I', candidate, PATCH_OFFSET, NEW_WORD)
    result = bytes(candidate)
    require(result[:CHANGED_BYTE] == raw[:CHANGED_BYTE]
            and result[CHANGED_BYTE + 1:] == raw[CHANGED_BYTE + 1:]
            and (raw[CHANGED_BYTE], result[CHANGED_BYTE]) == (0x05, 0x0d),
            'Patch must change exactly the one approved byte')
    require(elf_tables(result) == tables, 'ELF or PT/section header tables changed')
    require(digest(result[FUNCTION_OFFSET:FUNCTION_OFFSET + FUNCTION_SIZE])
            == PATCHED_FUNCTION_SHA256, 'Unexpected patched function SHA-256')
    require(digest(result) == PATCHED_SHA256, 'Unexpected full postimage SHA-256')
    restored = bytearray(result)
    struct.pack_into('<I', restored, PATCH_OFFSET, OLD_WORD)
    require(bytes(restored) == raw and digest(restored) == SOURCE_SHA256,
            'Inverse patch did not reconstruct the exact original library')
    return result


def self_test(raw: bytes, result: bytes) -> list[str]:
    """Exercise distinct guard paths, including rejection of a double patch."""
    require(((OLD_WORD >> 5) & 0xffff) == 0x382c
            and ((NEW_WORD >> 5) & 0xffff) == 0x386c
            and OLD_WORD ^ NEW_WORD == 0x40 << 5,
            'The instruction must add only the command-barrier immediate bit')
    cases = [
        ('foreign_preimage', 0x1000, raw[0x1000] ^ 1, 'full libunity.so preimage'),
        ('already_patched_instruction', CHANGED_BYTE, result[CHANGED_BYTE], 'barrier instruction'),
        ('changed_surrounding_function', FUNCTION_OFFSET, raw[FUNCTION_OFFSET] ^ 1, 'Surrounding matched'),
        ('wrong_elf_class', 4, 1, 'ELF64 little-endian'),
    ]
    passed = []
    for name, offset, value, expected_error in cases:
        bad = bytearray(raw)
        bad[offset] = value
        try:
            patch(bytes(bad))
        except ValueError as error:
            require(expected_error in str(error), name + ': wrong guard rejected input: ' + str(error))
        else:
            raise ValueError(name + ': malformed input was accepted')
        passed.append(name)
    return passed


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test-only', action='store_true', help='Validate without writing the private candidate')
    args = parser.parse_args()
    raw = INPUT.read_bytes()
    result = patch(raw)
    checks = self_test(raw, result)
    if args.self_test_only:
        print(json.dumps({'source_sha256': SOURCE_SHA256, 'postimage_sha256': PATCHED_SHA256,
                          'negative_guards_passed': checks, 'binary_written': False}, indent=2))
        return

    # No command-line path override: only the assigned private output is writable.
    require(OUTPUT.resolve() == OUTPUT.absolute() and MANIFEST.resolve() == MANIFEST.absolute(),
            'Private output may not redirect through a link')
    require(not OUTPUT.exists() or OUTPUT.read_bytes() == result,
            'Refusing to replace a different existing private library')
    header, ph, sh = elf_tables(raw)
    names = ('type', 'flags', 'offset', 'vaddr', 'paddr', 'filesz', 'memsz', 'align')
    manifest = {
        'input': str(INPUT), 'output': str(OUTPUT), 'bytes': len(raw),
        'source_sha256': SOURCE_SHA256, 'output_sha256': PATCHED_SHA256,
        'patcher_sha256': digest(Path(__file__).read_bytes()),
        'function_offset': hex(FUNCTION_OFFSET), 'function_size': FUNCTION_SIZE,
        'function_before_sha256': FUNCTION_SHA256, 'function_after_sha256': PATCHED_FUNCTION_SHA256,
        'instruction_offset': hex(PATCH_OFFSET), 'old_word': hex(OLD_WORD), 'new_word': hex(NEW_WORD),
        'changed_bytes': [{'offset': hex(CHANGED_BYTE), 'before': '05', 'after': '0d'}],
        'dispatch_call_target': hex(DISPATCH_TARGET), 'added_barrier_bit': '0x40',
        'elf_header_sha256': digest(header), 'program_headers_sha256': digest(ph),
        'section_headers_sha256': digest(sh),
        'unchanged_program_headers': [dict(zip(names, p)) for p in struct.iter_unpack('<IIQQQQQQ', ph)],
        'inverse_reconstructed_source_sha256': SOURCE_SHA256,
        'negative_guards_passed': checks, 'spec': SPEC_URL,
        'evidence': 'Historical private device report; excluded from source publication',
        'status': 'private candidate; hardware causality and acceptance untested',
    }
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(result)
    require(OUTPUT.read_bytes() == result, 'Private candidate reread differs')
    require(INPUT.read_bytes() == raw, 'Pinned vendor input changed during preparation')
    MANIFEST.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'output': str(OUTPUT), 'manifest': str(MANIFEST),
                      'sha256': PATCHED_SHA256, 'negative_guards_passed': checks}, indent=2))


if __name__ == '__main__':
    main()
