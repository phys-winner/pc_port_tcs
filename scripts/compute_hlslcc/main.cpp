#define main RasterToolMain
#include "../hlslcc_tool/main.cpp"
#undef main

struct ComputeReflection : Reflection
{
    void OnBufferBinding(const std::string& name, int slot, bool uav) override
    {
        entries.push_back("{\"kind\":\"storageBuffer\",\"name\":" + Json(name) + ",\"slot\":" + std::to_string(slot) + ",\"uav\":" + (uav ? "true" : "false") + "}");
    }
};

int main(int argc, char** argv)
{
    try
    {
        Require(argc == 5, "Usage: compute_hlslcc input.dxbc output.glsl reflection.json semantic.rdef");
        Bytes external = Read(argv[4]);
        ComputeReflection reflection;
        bool synthetic;
        uint32_t computeFlags = (flags & ~HLSLCC_FLAG_DISABLE_EXPLICIT_LOCATIONS) | HLSLCC_FLAG_UNIFORM_BUFFER_OBJECT | HLSLCC_FLAG_GLES31_IMAGE_QUALIFIERS | HLSLCC_FLAG_SAMPLER_PRECISION_ENCODED_IN_NAME;
        auto result = Translate(Read(argv[1]), computeFlags, reflection, synthetic, &external);
        Require(result.shaderType == 0x91b9, "DXBC is not a compute shader");
        Write(argv[2], result.sourceCode);
        std::ostringstream metadata;
        metadata << "{\"flags\":" << computeFlags << ",\"shaderType\":" << result.shaderType << ",\"events\":[";
        for (size_t i = 0; i < reflection.entries.size(); ++i) { if (i) metadata << ','; metadata << reflection.entries[i]; }
        metadata << "],\"textureSamplers\":[";
        for (size_t i = 0; i < result.textureSamplers.size(); ++i) { if (i) metadata << ','; metadata << Json(result.textureSamplers[i]); }
        metadata << "]}\n";
        Write(argv[3], metadata.str());
        std::cout << "COMPUTE_TRANSLATED " << result.sourceCode.size() << '\n';
        return 0;
    }
    catch (const std::exception& error) { std::cerr << "ERROR: " << error.what() << '\n'; return 1; }
}
