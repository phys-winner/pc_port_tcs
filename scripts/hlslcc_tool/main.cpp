#include "hlslcc.h"
#include "internal_includes/tokens.h"
#include <d3dcompiler.h>
#include <windows.h>
#include <algorithm>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <map>
#include <sstream>
#include <stdexcept>
#include <vector>

using Bytes = std::vector<char>;
static void Require(bool condition, const std::string& text) { if (!condition) throw std::runtime_error(text); }
static uint32_t U32(const Bytes& data, size_t offset)
{
    Require(offset <= data.size() && data.size() - offset >= 4, "Truncated DXBC field");
    uint32_t value; memcpy(&value, data.data() + offset, 4); return value;
}
static void Put(Bytes& data, size_t offset, uint32_t value)
{
    Require(offset <= data.size() && data.size() - offset >= 4, "Invalid generated field offset");
    memcpy(data.data() + offset, &value, 4);
}
static size_t Append(Bytes& data, const std::string& text)
{
    size_t offset = data.size(); data.insert(data.end(), text.begin(), text.end()); data.push_back(0);
    while (data.size() % 4) data.push_back(0);
    return offset;
}
static size_t Reserve(Bytes& data, size_t size) { size_t offset = data.size(); data.resize(offset + size, 0); return offset; }
static Bytes Read(const std::string& path)
{
    std::ifstream input(path, std::ios::binary); Require(input.good(), "Cannot open " + path);
    Bytes data((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    Require(!data.empty(), "Empty input " + path); return data;
}
static void Write(const std::string& path, const std::string& text)
{
    std::ofstream output(path, std::ios::binary); Require(output.good(), "Cannot create " + path);
    output.write(text.data(), text.size()); Require(output.good(), "Cannot write " + path);
}
static std::string Json(const std::string& value)
{
    std::ostringstream out; out << '"';
    for (unsigned char c : value)
    {
        if (c == '"' || c == '\\') out << '\\' << c;
        else if (c < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << (int)c;
        else out << c;
    }
    return out.str() + '"';
}

struct Chunk { uint32_t offset, size; std::string name; };
static std::vector<Chunk> Chunks(const Bytes& data)
{
    Require(data.size() >= 32 && memcmp(data.data(), "DXBC", 4) == 0, "Input must start with DXBC");
    Require(U32(data, 24) == data.size(), "DXBC size/header mismatch");
    uint32_t count = U32(data, 28);
    Require(count > 0 && count <= (data.size() - 32) / 4, "Invalid DXBC chunk table");
    std::vector<Chunk> chunks;
    for (uint32_t i = 0; i < count; ++i)
    {
        uint32_t offset = U32(data, 32 + i * 4), size = U32(data, offset + 4);
        Require(offset >= 32 + count * 4 && offset <= data.size() - 8 && size <= data.size() - offset - 8, "DXBC chunk outside input");
        chunks.push_back({ offset, size, std::string(data.data() + offset, 4) });
    }
    Require(std::count_if(chunks.begin(), chunks.end(), [](const Chunk& c) { return c.name == "SHDR" || c.name == "SHEX"; }) == 1, "Expected one shader token chunk");
    return chunks;
}

static Bytes WithRdef(const Bytes& source, const std::vector<Chunk>& originalChunks, const Bytes& rdef)
{
    Require(rdef.size() >= 24, "External RDEF is truncated");
    Require(U32(rdef, 4) <= rdef.size() && uint64_t(U32(rdef, 0)) * 24 <= rdef.size() - U32(rdef, 4), "Invalid RDEF constant-buffer table");
    Require(U32(rdef, 12) <= rdef.size() && uint64_t(U32(rdef, 8)) * 32 <= rdef.size() - U32(rdef, 12), "Invalid RDEF resource table");
    std::vector<Chunk> chunks;
    for (const auto& chunk : originalChunks) if (chunk.name != "RDEF") chunks.push_back(chunk);
    Bytes output(32 + (chunks.size() + 1) * 4, 0);
    memcpy(output.data(), source.data(), 32); memset(output.data() + 4, 0, 16);
    Put(output, 28, (uint32_t)chunks.size() + 1);
    for (size_t i = 0; i < chunks.size(); ++i)
    {
        Put(output, 32 + i * 4, (uint32_t)output.size());
        output.insert(output.end(), source.begin() + chunks[i].offset, source.begin() + chunks[i].offset + chunks[i].size + 8);
    }
    Put(output, 32 + chunks.size() * 4, (uint32_t)output.size());
    size_t at = Reserve(output, 8); memcpy(output.data() + at, "RDEF", 4); Put(output, at + 4, (uint32_t)rdef.size());
    output.insert(output.end(), rdef.begin(), rdef.end()); Put(output, 24, (uint32_t)output.size());
    return output;
}

// shortcut: these names expose registers only; use semantic JSON/RDEF before Unity integration.
static Bytes RestoreRegisterReflection(const Bytes& source, const std::vector<Chunk>& chunks)
{
    auto shader = *std::find_if(chunks.begin(), chunks.end(), [](const Chunk& c) { return c.name == "SHDR" || c.name == "SHEX"; });
    Require(shader.size % 4 == 0 && shader.size >= 8, "Invalid shader tokens");
    uint32_t version = U32(source, shader.offset + 8), count = U32(source, shader.offset + 12);
    Require(count * uint64_t(4) == shader.size, "Shader token count mismatch");
    Require(DecodeProgramMajorVersion(version) >= 4 && DecodeProgramMajorVersion(version) <= 5 && DecodeProgramMinorVersion(version) == 0, "Register reflection supports shader models 4.0/5.0");
    struct Resource { uint32_t type, slot, dimension, result; std::string name; };
    std::map<uint32_t, uint32_t> buffers;
    std::vector<Resource> resources;
    for (uint32_t at = 2; at < count;)
    {
        auto token = U32(source, shader.offset + 8 + at * 4);
        auto opcode = DecodeOpcodeType(token);
        uint32_t length = DecodeInstructionLength(token);
        if (opcode == OPCODE_CUSTOMDATA) { Require(at + 1 < count, "Truncated custom-data token"); length = U32(source, shader.offset + 8 + (at + 1) * 4); }
        Require(length > 0 && length <= count - at, "Invalid instruction extent");
        if (opcode == OPCODE_DCL_RESOURCE_STRUCTURED || opcode == OPCODE_DCL_RESOURCE_RAW || (opcode >= OPCODE_DCL_UNORDERED_ACCESS_VIEW_TYPED && opcode <= OPCODE_DCL_UNORDERED_ACCESS_VIEW_STRUCTURED))
            throw std::runtime_error("Stripped structured/UAV reflection requires Unity parameter metadata");
        if (opcode == OPCODE_DCL_CONSTANT_BUFFER || opcode == OPCODE_DCL_RESOURCE || opcode == OPCODE_DCL_SAMPLER)
        {
            uint32_t operandAt = at + 1;
            if (DecodeIsOpcodeExtended(token)) ++operandAt;
            Require(operandAt < at + length, "Truncated declaration operand");
            uint32_t operand = U32(source, shader.offset + 8 + operandAt * 4);
            Require(!DecodeIsOperandExtended(operand) && DecodeOperandIndexRepresentation(0, operand) == OPERAND_INDEX_IMMEDIATE32, "Unsupported declaration index encoding");
            uint32_t slot = U32(source, shader.offset + 8 + (operandAt + 1) * 4);
            Require(slot < MAX_RESOURCE_BINDINGS, "Resource slot exceeds HLSLcc bound");
            if (opcode == OPCODE_DCL_CONSTANT_BUFFER)
            {
                Require(operandAt + 2 < at + length, "Truncated constant-buffer declaration");
                uint32_t vectors = U32(source, shader.offset + 8 + (operandAt + 2) * 4);
                Require(vectors > 0 && vectors <= 4096, "Invalid constant-buffer size");
                buffers[slot] = vectors;
                resources.push_back({ RTYPE_CBUFFER, slot, 0, RETURN_TYPE_FLOAT, "cb" + std::to_string(slot) });
            }
            else if (opcode == OPCODE_DCL_SAMPLER) resources.push_back({ RTYPE_SAMPLER, slot, 0, 0, "s" + std::to_string(slot) });
            else
            {
                const uint32_t dimensions[] = { 0, 1, 2, 4, 6, 8, 9, 3, 5, 7, 10 };
                uint32_t dimension = DecodeResourceDimension(token);
                Require(dimension < sizeof(dimensions) / sizeof(dimensions[0]), "Unsupported texture dimension");
                uint32_t result = U32(source, shader.offset + 8 + (at + length - 1) * 4) & 15;
                resources.push_back({ RTYPE_TEXTURE, slot, dimensions[dimension], result, "t" + std::to_string(slot) });
            }
        }
        at += length;
    }
    Bytes rdef(28, 0);
    size_t bufferTable = Reserve(rdef, buffers.size() * 24), resourceTable = Reserve(rdef, resources.size() * 32);
    Put(rdef, 0, (uint32_t)buffers.size()); Put(rdef, 4, (uint32_t)bufferTable);
    Put(rdef, 8, (uint32_t)resources.size()); Put(rdef, 12, (uint32_t)resourceTable); Put(rdef, 16, version);
    size_t index = 0;
    for (const auto& pair : buffers)
    {
        std::string name = "cb" + std::to_string(pair.first);
        size_t nameAt = Append(rdef, name), variableNameAt = Append(rdef, name + "_data");
        size_t typeAt = Reserve(rdef, 16);
        uint16_t type[] = { SVC_VECTOR, SVT_FLOAT, 1, 4, (uint16_t)pair.second, 0 };
        memcpy(rdef.data() + typeAt, type, sizeof(type));
        size_t variableAt = Reserve(rdef, DecodeProgramMajorVersion(version) >= 5 ? 40 : 24);
        Put(rdef, variableAt, (uint32_t)variableNameAt); Put(rdef, variableAt + 8, pair.second * 16); Put(rdef, variableAt + 16, (uint32_t)typeAt);
        size_t at = bufferTable + index++ * 24;
        Put(rdef, at, (uint32_t)nameAt); Put(rdef, at + 4, 1); Put(rdef, at + 8, (uint32_t)variableAt); Put(rdef, at + 12, pair.second * 16);
    }
    for (size_t i = 0; i < resources.size(); ++i)
    {
        const auto& resource = resources[i]; size_t at = resourceTable + i * 32;
        Put(rdef, at, (uint32_t)Append(rdef, resource.name)); Put(rdef, at + 4, resource.type); Put(rdef, at + 8, resource.result);
        Put(rdef, at + 12, resource.dimension); Put(rdef, at + 16, 0xffffffff); Put(rdef, at + 20, resource.slot); Put(rdef, at + 24, 1);
    }
    return WithRdef(source, chunks, rdef);
}

struct Reflection : HLSLccReflection
{
    std::vector<std::string> entries;
    void OnDiagnostics(const std::string& error, int line, bool failure) override { entries.push_back("{\"kind\":\"diagnostic\",\"error\":" + std::string(failure ? "true" : "false") + ",\"line\":" + std::to_string(line) + ",\"text\":" + Json(error) + "}"); }
    void OnInputBinding(const std::string& name, int slot) override { entries.push_back("{\"kind\":\"input\",\"name\":" + Json(name) + ",\"slot\":" + std::to_string(slot) + "}"); }
    bool OnConstantBuffer(const std::string& name, size_t size, size_t members) override { entries.push_back("{\"kind\":\"cbuffer\",\"name\":" + Json(name) + ",\"size\":" + std::to_string(size) + ",\"members\":" + std::to_string(members) + "}"); return true; }
    bool OnConstant(const std::string& name, int slot, SHADER_VARIABLE_TYPE type, int rows, int cols, bool matrix, int array, bool used) override { entries.push_back("{\"kind\":\"constant\",\"name\":" + Json(name) + ",\"slot\":" + std::to_string(slot) + ",\"type\":" + std::to_string(type) + ",\"rows\":" + std::to_string(rows) + ",\"columns\":" + std::to_string(cols) + ",\"array\":" + std::to_string(array) + ",\"used\":" + (used ? "true" : "false") + "}"); return true; }
    void OnConstantBufferBinding(const std::string& name, int slot) override { entries.push_back("{\"kind\":\"cbufferBinding\",\"name\":" + Json(name) + ",\"slot\":" + std::to_string(slot) + "}"); }
    void OnTextureBinding(const std::string& name, int slot, int sampler, bool multi, HLSLCC_TEX_DIMENSION dimension, bool uav) override { entries.push_back("{\"kind\":\"texture\",\"name\":" + Json(name) + ",\"slot\":" + std::to_string(slot) + ",\"sampler\":" + std::to_string(sampler) + ",\"dimension\":" + std::to_string(dimension) + ",\"uav\":" + (uav ? "true" : "false") + "}"); }
};

static GLSLShader Translate(const Bytes& original, uint32_t flags, Reflection& reflection, bool& synthetic, const Bytes* external)
{
    auto chunks = Chunks(original);
    synthetic = external == nullptr && std::none_of(chunks.begin(), chunks.end(), [](const Chunk& c) { return c.name == "RDEF"; });
    Bytes data = external != nullptr ? WithRdef(original, chunks, *external) : (synthetic ? RestoreRegisterReflection(original, chunks) : original);
    GLSLShader result; GlExtensions extensions = {}; HLSLccSamplerPrecisionInfo precision;
    Require(TranslateHLSLFromMem(data.data(), flags, LANG_ES_310, &extensions, nullptr, precision, reflection, &result) != 0, "HLSLcc translation failed");
    Require(result.sourceCode.find("#version 310 es") != std::string::npos, "Unexpected target GLSL version");
    return result;
}

static const uint32_t flags = HLSLCC_FLAG_MOBILE_TARGET | HLSLCC_FLAG_COMBINE_TEXTURE_SAMPLERS | HLSLCC_FLAG_INOUT_SEMANTIC_NAMES | HLSLCC_FLAG_DISABLE_EXPLICIT_LOCATIONS | HLSLCC_FLAG_DISABLE_GLOBALS_STRUCT | HLSLCC_FLAG_TRANSLATE_MATRICES;
static void Convert(const Bytes& data, const std::string& output, const std::string& json, const Bytes* external = nullptr, bool ubo = false)
{
    Reflection reflection; bool synthetic;
    uint32_t actualFlags = flags;
    if (ubo) actualFlags |= HLSLCC_FLAG_UNIFORM_BUFFER_OBJECT | HLSLCC_FLAG_GLOBAL_CONSTS_NEVER_IN_UBO;
    auto result = Translate(data, actualFlags, reflection, synthetic, external);
    Write(output, result.sourceCode);
    std::ostringstream meta; meta << "{\"target\":\"glsl-es-310\",\"flags\":" << actualFlags << ",\"uniformBufferObjects\":" << (ubo ? "true" : "false") << ",\"shaderType\":" << result.shaderType << ",\"syntheticRdef\":" << (synthetic ? "true" : "false") << ",\"reflectionMode\":" << Json(external != nullptr ? "external" : (synthetic ? "registerOnly" : "original")) << ",\"events\":[";
    for (size_t i = 0; i < reflection.entries.size(); ++i) { if (i) meta << ','; meta << reflection.entries[i]; }
    meta << "],\"textureSamplers\":[";
    for (size_t i = 0; i < result.textureSamplers.size(); ++i) { if (i) meta << ','; meta << Json(result.textureSamplers[i]); }
    meta << "],\"constantBuffers\":[";
    for (size_t i = 0; i < result.reflection.psConstantBuffers.size(); ++i)
    {
        const auto& buffer = result.reflection.psConstantBuffers[i];
        if (i) meta << ',';
        meta << "{\"name\":" << Json(buffer.name) << ",\"size\":" << buffer.ui32TotalSizeInBytes << ",\"variables\":[";
        for (size_t j = 0; j < buffer.asVars.size(); ++j)
        {
            const auto& variable = buffer.asVars[j]; if (j) meta << ',';
            meta << "{\"name\":" << Json(variable.name) << ",\"offset\":" << variable.ui32StartOffset << ",\"size\":" << variable.ui32Size << ",\"class\":" << variable.sType.Class << ",\"type\":" << variable.sType.Type << ",\"rows\":" << variable.sType.Rows << ",\"columns\":" << variable.sType.Columns << ",\"elements\":" << variable.sType.Elements << '}';
        }
        meta << "]}";
    }
    meta << "],\"resources\":[";
    for (size_t i = 0; i < result.reflection.psResourceBindings.size(); ++i)
    {
        const auto& resource = result.reflection.psResourceBindings[i]; if (i) meta << ',';
        meta << "{\"name\":" << Json(resource.name) << ",\"type\":" << resource.eType << ",\"slot\":" << resource.ui32BindPoint << ",\"count\":" << resource.ui32BindCount << ",\"dimension\":" << resource.eDimension << ",\"returnType\":" << resource.ui32ReturnType << '}';
    }
    meta << "]}\n"; Write(json, meta.str());
    std::cout << "PASS shaderType=" << result.shaderType << " syntheticRdef=" << synthetic << " bytes=" << result.sourceCode.size() << '\n';
}

static void SelfTest(const std::string& directory)
{
    CreateDirectoryA(directory.c_str(), nullptr);
    const char* source = "cbuffer Globals : register(b0) { float4x4 mvp; }; float4 main(float4 p : POSITION) : SV_POSITION { return mul(mvp,p); }";
    ID3DBlob* code = nullptr; ID3DBlob* errors = nullptr;
    HRESULT status = D3DCompile(source, strlen(source), "selftest", nullptr, nullptr, "main", "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    std::string diagnostic = errors ? std::string((char*)errors->GetBufferPointer(), errors->GetBufferSize()) : "D3DCompile failed";
    if (errors) errors->Release(); Require(SUCCEEDED(status), diagnostic);
    Bytes data((char*)code->GetBufferPointer(), (char*)code->GetBufferPointer() + code->GetBufferSize());
    code->Release(); Write(directory + "/full.dxbc", std::string(data.begin(), data.end())); Convert(data, directory + "/full.vert", directory + "/full.json");
    ID3DBlob* stripped = nullptr;
    Require(SUCCEEDED(D3DStripShader(data.data(), data.size(), D3DCOMPILER_STRIP_REFLECTION_DATA, &stripped)), "D3DStripShader failed");
    Bytes minimal((char*)stripped->GetBufferPointer(), (char*)stripped->GetBufferPointer() + stripped->GetBufferSize());
    stripped->Release(); Write(directory + "/stripped.dxbc", std::string(minimal.begin(), minimal.end())); Convert(minimal, directory + "/stripped.vert", directory + "/stripped.json");
    auto fullChunks = Chunks(data);
    auto rdef = *std::find_if(fullChunks.begin(), fullChunks.end(), [](const Chunk& c) { return c.name == "RDEF"; });
    Bytes external(data.begin() + rdef.offset + 8, data.begin() + rdef.offset + 8 + rdef.size);
    Convert(minimal, directory + "/external.vert", directory + "/external.json", &external);
    bool rejected = false; try { Chunks(Bytes{ 'D', 'X', 'B', 'C' }); } catch (const std::exception&) { rejected = true; }
    Require(rejected, "Truncated DXBC was accepted");
    std::cout << "PASS self-test: reflected and stripped DXBC; malformed input rejected\n";
}

int main(int argc, char** argv)
{
    try
    {
        if (argc == 3 && std::string(argv[1]) == "--self-test") { SelfTest(argv[2]); return 0; }
        bool ubo = argc > 1 && std::string(argv[argc - 1]) == "--ubo";
        int arguments = argc - (ubo ? 1 : 0);
        Require(arguments == 4 || arguments == 5, "Usage: hlslcc_tool input.dxbc output.glsl reflection.json [semantic.rdef] [--ubo]");
        Bytes external; if (arguments == 5) external = Read(argv[4]);
        Convert(Read(argv[1]), argv[2], argv[3], arguments == 5 ? &external : nullptr, ubo); return 0;
    }
    catch (const std::exception& error) { std::cerr << "ERROR: " << error.what() << '\n'; return 1; }
}
