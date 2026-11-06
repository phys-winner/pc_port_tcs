#define _GNU_SOURCE 1
#include <android/log.h>
#include <dlfcn.h>
#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <jni.h>
#include <link.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

#include "unity_preimage.h"

_Static_assert(sizeof(void *) == 4 && sizeof(long) == 4, "ARMv7 long32 ABI required");

typedef int (*Seek32)(FILE *, long, int);
typedef long (*Tell32)(FILE *);
typedef int (*Seek64)(FILE *, off64_t, int);
typedef off64_t (*Tell64)(FILE *);
static Seek32 original_seek;
static Tell32 original_tell;
static Seek64 real_seek64;
static Tell64 real_tell64;
static void **seek_slot, **tell_slot;
static int installation_state;
static int seek_logged, tell_logged;
static void *unity_preload_handle;
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "ClosingShiftOBBIO", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "ClosingShiftOBBIO", __VA_ARGS__)

static long pattern32(uint32_t value) {
    long result;
    memcpy(&result, &value, sizeof(result));
    return result;
}

static int seek_compat(FILE *stream, long offset, int origin) {
    /* ZIP32 absolute offsets use every bit of long32; relative seeks remain signed. */
    off64_t wide = origin == SEEK_SET ? (off64_t)(uint32_t)offset : (off64_t)offset;
    if (origin == SEEK_SET && (uint32_t)offset >= UINT32_C(0x80000000) &&
        !__atomic_exchange_n(&seek_logged, 1, __ATOMIC_RELAXED))
        LOGI("First ZIP32 absolute seek above 2 GiB: %llu", (unsigned long long)wide);
    return real_seek64(stream, wide, origin);
}

static long tell_compat(FILE *stream) {
    off64_t position = real_tell64(stream);
    if (position < 0)
        return -1;
    if ((uint64_t)position > UINT32_MAX) {
        errno = EOVERFLOW;
        return -1;
    }
    if ((uint64_t)position >= UINT32_C(0x80000000) &&
        !__atomic_exchange_n(&tell_logged, 1, __ATOMIC_RELAXED))
        LOGI("First ZIP32 tell above 2 GiB: %llu", (unsigned long long)position);
    return pattern32((uint32_t)position);
}

struct UnityImage {
    uintptr_t base;
    const Elf32_Phdr *phdr;
    size_t count;
};

static int find_unity(struct dl_phdr_info *info, size_t size, void *opaque) {
    (void)size;
    const char *name = strrchr(info->dlpi_name, '/');
    name = name ? name + 1 : info->dlpi_name;
    if (strcmp(name, "libunity.so") != 0)
        return 0;
    struct UnityImage *image = opaque;
    image->base = info->dlpi_addr;
    image->phdr = info->dlpi_phdr;
    image->count = info->dlpi_phnum;
    return 1;
}

static int in_image(const struct UnityImage *image, uintptr_t address, size_t bytes) {
    if (address + bytes < address)
        return 0;
    for (size_t i = 0; i < image->count; ++i) {
        const Elf32_Phdr *p = &image->phdr[i];
        if (p->p_type == PT_LOAD && address >= image->base + p->p_vaddr &&
            address + bytes <= image->base + p->p_vaddr + p->p_memsz)
            return 1;
    }
    return 0;
}

static int check_preimage(const struct UnityImage *image) {
    const Elf32_Ehdr *eh = (const Elf32_Ehdr *)image->base;
    if (!in_image(image, image->base, sizeof(*eh)) ||
        memcmp(eh->e_ident, ELFMAG, SELFMAG) || eh->e_ident[EI_CLASS] != ELFCLASS32 ||
        eh->e_machine != EM_ARM || image->count > 128)
        return -1;
    int build_id_ok = 0;
    for (size_t i = 0; i < image->count; ++i) {
        const Elf32_Phdr *p = &image->phdr[i];
        if (p->p_type != PT_NOTE || p->p_memsz > 65536)
            continue;
        uintptr_t cursor = image->base + p->p_vaddr;
        uintptr_t end = cursor + p->p_memsz;
        if (!in_image(image, cursor, p->p_memsz))
            return -2;
        while (cursor + sizeof(Elf32_Nhdr) <= end) {
            const Elf32_Nhdr *note = (const Elf32_Nhdr *)cursor;
            size_t names = ((size_t)note->n_namesz + 3) & ~(size_t)3;
            size_t desc = ((size_t)note->n_descsz + 3) & ~(size_t)3;
            if (names > 65536 || desc > 65536 || cursor + sizeof(*note) + names + desc > end)
                return -3;
            const unsigned char *name = (const unsigned char *)(note + 1);
            if (note->n_type == NT_GNU_BUILD_ID && note->n_namesz == 4 &&
                !memcmp(name, "GNU", 4) && note->n_descsz == sizeof(expected_build_id) &&
                !memcmp(name + names, expected_build_id, sizeof(expected_build_id)))
                build_id_ok = 1;
            cursor += sizeof(*note) + names + desc;
        }
    }
    if (!build_id_ok)
        return -4;
    for (size_t i = 0; i < sizeof(code_guards) / sizeof(code_guards[0]); ++i) {
        const struct CodeGuard *guard = &code_guards[i];
        uintptr_t address = image->base + guard->offset;
        if (!in_image(image, address, sizeof(guard->bytes)) ||
            memcmp((const void *)address, guard->bytes, sizeof(guard->bytes)))
            return -5;
    }
    return 0;
}

static int locate_slots(const struct UnityImage *image) {
    const Elf32_Dyn *dynamic = NULL;
    size_t dynamic_count = 0;
    for (size_t i = 0; i < image->count; ++i)
        if (image->phdr[i].p_type == PT_DYNAMIC) {
            dynamic = (const Elf32_Dyn *)(image->base + image->phdr[i].p_vaddr);
            dynamic_count = image->phdr[i].p_memsz / sizeof(*dynamic);
        }
    if (!dynamic || dynamic_count > 4096 ||
        !in_image(image, (uintptr_t)dynamic, dynamic_count * sizeof(*dynamic)))
        return -1;
    const Elf32_Sym *symbols = NULL;
    const char *strings = NULL;
    const Elf32_Rel *relocations = NULL;
    size_t string_size = 0, relocation_size = 0;
    unsigned rel_kind = 0;
    for (size_t i = 0; i < dynamic_count && dynamic[i].d_tag != DT_NULL; ++i) {
        uintptr_t ptr = image->base + dynamic[i].d_un.d_ptr;
        switch (dynamic[i].d_tag) {
        case DT_SYMTAB: symbols = (const Elf32_Sym *)ptr; break;
        case DT_STRTAB: strings = (const char *)ptr; break;
        case DT_STRSZ: string_size = dynamic[i].d_un.d_val; break;
        case DT_JMPREL: relocations = (const Elf32_Rel *)ptr; break;
        case DT_PLTRELSZ: relocation_size = dynamic[i].d_un.d_val; break;
        case DT_PLTREL: rel_kind = dynamic[i].d_un.d_val; break;
        }
    }
    if (!symbols || !strings || !relocations || rel_kind != DT_REL ||
        relocation_size % sizeof(*relocations) || relocation_size > 1048576 ||
        !in_image(image, (uintptr_t)strings, string_size) ||
        !in_image(image, (uintptr_t)relocations, relocation_size))
        return -2;
    for (size_t i = 0; i < relocation_size / sizeof(*relocations); ++i) {
        const Elf32_Rel *rel = &relocations[i];
        if (ELF32_R_TYPE(rel->r_info) != R_ARM_JUMP_SLOT)
            continue;
        size_t index = ELF32_R_SYM(rel->r_info);
        uintptr_t symbol_address = (uintptr_t)symbols + index * sizeof(*symbols);
        if (index > 1048576 || !in_image(image, symbol_address, sizeof(*symbols)))
            return -3;
        const Elf32_Sym *symbol = (const Elf32_Sym *)symbol_address;
        if (symbol->st_name >= string_size ||
            !memchr(strings + symbol->st_name, 0, string_size - symbol->st_name))
            return -4;
        const char *name = strings + symbol->st_name;
        if (strcmp(name, "fseek") && strcmp(name, "ftell"))
            continue;
        void **slot = (void **)(image->base + rel->r_offset);
        if (!in_image(image, (uintptr_t)slot, sizeof(*slot)))
            return -5;
        if (!strcmp(name, "fseek")) {
            if (seek_slot || rel->r_offset != EXPECTED_FSEEK_SLOT)
                return -6;
            seek_slot = slot;
        } else {
            if (tell_slot || rel->r_offset != EXPECTED_FTELL_SLOT)
                return -7;
            tell_slot = slot;
        }
    }
    return seek_slot && tell_slot ? 0 : -8;
}

static int page_protection(uintptr_t address) {
    FILE *maps = fopen("/proc/self/maps", "r");
    if (!maps)
        return -1;
    char line[1024], access[5];
    unsigned long start, end;
    int result = -1;
    for (unsigned i = 0; i < 16384 && fgets(line, sizeof(line), maps); ++i)
        if (sscanf(line, "%lx-%lx %4s", &start, &end, access) == 3 &&
            address >= start && address < end) {
            result = (access[0] == 'r' ? PROT_READ : 0) |
                     (access[1] == 'w' ? PROT_WRITE : 0) |
                     (access[2] == 'x' ? PROT_EXEC : 0);
            break;
        }
    fclose(maps);
    return result;
}

static int patch_slots(void) {
    original_seek = (Seek32)dlsym(RTLD_DEFAULT, "fseek");
    original_tell = (Tell32)dlsym(RTLD_DEFAULT, "ftell");
    real_seek64 = (Seek64)dlsym(RTLD_DEFAULT, "fseeko64");
    real_tell64 = (Tell64)dlsym(RTLD_DEFAULT, "ftello64");
    if (!original_seek || !original_tell || !real_seek64 || !real_tell64 ||
        *seek_slot != (void *)original_seek ||
        *tell_slot != (void *)original_tell)
        return -1;
    long page_size = sysconf(_SC_PAGESIZE);
    if (page_size < 4096 || page_size > 65536 || (page_size & (page_size - 1)))
        return -2;
    uintptr_t pages[2] = {(uintptr_t)seek_slot & ~(uintptr_t)(page_size - 1),
                          (uintptr_t)tell_slot & ~(uintptr_t)(page_size - 1)};
    int count = pages[0] == pages[1] ? 1 : 2;
    int protections[2];
    for (int i = 0; i < count; ++i) {
        protections[i] = page_protection(pages[i]);
        if (protections[i] < 0 || (protections[i] & PROT_EXEC))
            return -3;
    }
    for (int i = 0; i < count; ++i)
        if (mprotect((void *)pages[i], page_size, protections[i] | PROT_WRITE)) {
            for (int j = 0; j < i; ++j)
                mprotect((void *)pages[j], page_size, protections[j]);
            return -4;
        }
    __atomic_store_n(seek_slot, (void *)seek_compat, __ATOMIC_RELEASE);
    __atomic_store_n(tell_slot, (void *)tell_compat, __ATOMIC_RELEASE);
    int result = 0;
    for (int i = 0; i < count; ++i)
        if (mprotect((void *)pages[i], page_size, protections[i]))
            result = -5;
    return result;
}

static int probe(const char *directory) {
    char path[4096];
    if (!directory || directory[0] != '/' ||
        snprintf(path, sizeof(path), "%s/closing_shift_obb_io_XXXXXX", directory) >= (int)sizeof(path))
        return -1;
    int fd = mkstemp(path);
    if (fd < 0)
        return -2;
    /* The sparse fixture has no persistent directory entry, including on crash. */
    unlink(path);
    FILE *file = fdopen(fd, "w+b");
    if (!file) { close(fd); return -3; }
    static const unsigned char marker_a[16] = "ZIP32_BOUNDARY_A";
    static const unsigned char marker_b[16] = "ZIP32_BOUNDARY_B";
    const uint32_t position_a = UINT32_C(0x7ffffff8);
    const uint32_t position_b = UINT32_C(0xe800007b); /* 3.625 GiB + 123 */
    int result = -4;
    if (real_seek64(file, position_a, SEEK_SET) || fwrite(marker_a, 1, 16, file) != 16 ||
        real_seek64(file, position_b, SEEK_SET) || fwrite(marker_b, 1, 16, file) != 16 || fflush(file))
        goto done;
    Seek32 seek = (Seek32)__atomic_load_n(seek_slot, __ATOMIC_ACQUIRE);
    Tell32 tell = (Tell32)__atomic_load_n(tell_slot, __ATOMIC_ACQUIRE);
    unsigned char buffer[16];
    result = -5;
    if (seek(file, pattern32(position_a), SEEK_SET) ||
        fread(buffer, 1, 16, file) != 16 || memcmp(buffer, marker_a, 16) ||
        (uint32_t)tell(file) != position_a + 16)
        goto done;
    result = -6;
    if (seek(file, pattern32(position_b), SEEK_SET) ||
        (uint32_t)tell(file) != position_b || fread(buffer, 1, 16, file) != 16 ||
        memcmp(buffer, marker_b, 16) || (uint32_t)tell(file) != position_b + 16 ||
        seek(file, -16, SEEK_CUR) || (uint32_t)tell(file) != position_b ||
        seek(file, -16, SEEK_END) || (uint32_t)tell(file) != position_b)
        goto done;
    result = -7;
    if (real_seek64(file, INT64_C(0x100000010), SEEK_SET))
        goto done;
    errno = 0;
    if (tell(file) != -1 || errno != EOVERFLOW)
        goto done;
    result = 0;
done:
    fclose(file);
    return result;
}

JNIEXPORT jint JNICALL Java_com_chillasart_theclosingshift_port_LargeObbIO_install(
        JNIEnv *env, jclass type, jstring cache_directory) {
    (void)type;
    int previous = 0;
    if (!__atomic_compare_exchange_n(&installation_state, &previous, 1, 0,
                                     __ATOMIC_ACQUIRE, __ATOMIC_RELAXED))
        return previous == 2 ? 0 : -10;
    struct UnityImage image = {0};
    int result = -11;
    dl_iterate_phdr(find_unity, &image);
    if (!image.base) {
        /* Only maps the native ELF; Unity's normal NativeLoader invokes JNI_OnLoad later. */
        unity_preload_handle = dlopen("libunity.so", RTLD_NOW | RTLD_LOCAL);
        if (!unity_preload_handle) goto failed;
        dl_iterate_phdr(find_unity, &image);
    }
    if (!image.base) goto failed;
    result = check_preimage(&image);
    if (result) { result -= 100; goto failed; }
    result = locate_slots(&image);
    if (result) { result -= 200; goto failed; }
    result = patch_slots();
    if (result) { result -= 300; goto failed; }
    const char *directory = cache_directory ? (*env)->GetStringUTFChars(env, cache_directory, NULL) : NULL;
    if (!directory) { result = -401; goto failed; }
    result = probe(directory);
    (*env)->ReleaseStringUTFChars(env, cache_directory, directory);
    if (result) { result -= 400; goto failed; }
    __atomic_store_n(&installation_state, 2, __ATOMIC_RELEASE);
    LOGI("ARMv7 ZIP32 IO installed; sparse app-UID probe PASS at 2 GiB and 3.625 GiB; source %s", UNITY_SHA256);
    return 0;
failed:
    __atomic_store_n(&installation_state, -1, __ATOMIC_RELEASE);
    LOGE("ARMv7 ZIP32 IO rejected or probe failed: %d errno=%d", result, errno);
    return result;
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
    (void)vm; (void)reserved;
    return JNI_VERSION_1_6;
}
