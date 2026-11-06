#include <jni.h>
#include <dlfcn.h>
#include <stdint.h>
#include <string.h>
#include "il2cpp-api.h"

static void *library;
static uint32_t original_array;
static int original_mode;

extern "C" JNIEXPORT jboolean JNICALL Java_com_chillasart_tools_PortAudit_frameRate(JNIEnv *env, jclass, jint value) {
    if (value != 0 && (value < 5 || value > 8)) {
        env->ThrowNew(env->FindClass("java/lang/IllegalArgumentException"), "QA FPS outside 5..8");
        return false;
    }
    if (!library) library = dlopen("libil2cpp.so", RTLD_NOW | RTLD_NOLOAD);
    if (!library) return false;
#define API(name) auto name##_fn = reinterpret_cast<decltype(&name)>(dlsym(library, #name)); if (!name##_fn) { env->ThrowNew(env->FindClass("java/lang/IllegalStateException"), "Missing " #name); return false; }
    API(il2cpp_domain_get); API(il2cpp_thread_attach); API(il2cpp_domain_assembly_open);
    API(il2cpp_assembly_get_image); API(il2cpp_class_from_name); API(il2cpp_class_get_field_from_name);
    API(il2cpp_field_static_get_value); API(il2cpp_field_static_set_value);
    API(il2cpp_get_corlib); API(il2cpp_array_new); API(il2cpp_array_object_header_size);
    API(il2cpp_gchandle_new); API(il2cpp_gchandle_get_target); API(il2cpp_gchandle_free);
#undef API
    Il2CppDomain *domain = il2cpp_domain_get_fn();
    if (!domain) return false;
    il2cpp_thread_attach_fn(domain);
    const Il2CppAssembly *assembly = il2cpp_domain_assembly_open_fn(domain, "ClosingShift.Android.dll");
    if (!assembly) return false;
    Il2CppClass *settings = il2cpp_class_from_name_fn(il2cpp_assembly_get_image_fn(assembly), "ClosingShift.Android", "AndroidInputSettings");
    if (!settings) return false;
    FieldInfo *array = il2cpp_class_get_field_from_name_fn(settings, "FpsValues");
    FieldInfo *mode = il2cpp_class_get_field_from_name_fn(settings, "FpsMode");
    if (!array || !mode) return false;
    if (value == 0) {
        if (!original_array) return false;
        Il2CppObject *restored = il2cpp_gchandle_get_target_fn(original_array);
        il2cpp_field_static_set_value_fn(array, restored);
        il2cpp_field_static_set_value_fn(mode, &original_mode);
        il2cpp_gchandle_free_fn(original_array); original_array = 0;
        return true;
    }
    if (original_array) return true;
    Il2CppObject *old = nullptr;
    il2cpp_field_static_get_value_fn(array, &old);
    if (!old) return false;
    il2cpp_field_static_get_value_fn(mode, &original_mode);
    original_array = il2cpp_gchandle_new_fn(old, false);
    Il2CppClass *integer = il2cpp_class_from_name_fn(il2cpp_get_corlib_fn(), "System", "Int32");
    Il2CppArray *replacement = integer ? il2cpp_array_new_fn(integer, 2) : nullptr;
    if (!replacement) {
        il2cpp_gchandle_free_fn(original_array); original_array = 0;
        return false;
    }
    int32_t values[] = {value, 0};
    memcpy(reinterpret_cast<char *>(replacement) + il2cpp_array_object_header_size_fn(), values, sizeof(values));
    // This Unity API takes a reference value directly, while scalar fields take its address.
    il2cpp_field_static_set_value_fn(array, replacement);
    int zero = 0;
    il2cpp_field_static_set_value_fn(mode, &zero);
    Il2CppArray *readback = nullptr;
    il2cpp_field_static_get_value_fn(array, &readback);
    if (readback != replacement) {
        env->ThrowNew(env->FindClass("java/lang/IllegalStateException"), "QA FPS field readback differs");
        return false;
    }
    return true;
}
