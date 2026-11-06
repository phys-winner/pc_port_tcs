"""Read-only texture residency/mip topology and source mesh UV metric audit."""
from collections import Counter, defaultdict
from pathlib import Path
import hashlib
import json
import math
import UnityPy

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r"external/game/TheClosingShift_Data")
OUT = ROOT / "work/evidence/texture_memory"


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def normalized(path):
    return path.replace("\\", "/")


def main():
    inventory_path = ROOT / "work/evidence/asset_inventory/inventory.json"
    plan_path = ROOT / "work/evidence/asset_inventory/texture_conversion_plan_complete_alpha.json"
    inventory = json.loads(inventory_path.read_text())
    plan = json.loads(plan_path.read_text())
    targets = {(normalized(t["file"]), t["path_id"]): t for t in plan["textures"]}
    externals = {normalized(k): list(map(normalized, v)) for k, v in json.loads((ROOT / "work/evidence/asset_inventory/externals.json").read_text()).items()}
    shaders = {(normalized(t["file"]), t["path_id"]): t["name"] for t in inventory["shaders"]}
    usage = defaultdict(list)

    def resolve(owner, pointer):
        file_id, path_id = pointer["m_FileID"], pointer["m_PathID"]
        if not path_id:
            return None
        filename = owner if not file_id else externals[owner][file_id - 1]
        return (filename, path_id)

    for material in inventory["materials"]:
        owner = normalized(material["file"])
        shader = shaders.get(resolve(owner, material["m_Shader"]), "UNKNOWN")
        for name, texture in material["m_SavedProperties"].get("m_TexEnvs", []):
            key = resolve(owner, texture["m_Texture"])
            if key:
                usage[key].append({"file": owner, "materialId": material["path_id"], "material": material["m_Name"],
                    "shader": shader, "property": name, "scale": texture.get("m_Scale"), "offset": texture.get("m_Offset")})

    groups = defaultdict(lambda: defaultdict(int))
    textures = []
    stock = {"Standard", "Bakery/Standard", "Autodesk Interactive", "Legacy Shaders/Diffuse", "Legacy Shaders/Bumped Diffuse",
        "Legacy Shaders/Specular", "Legacy Shaders/Bumped Specular", "Legacy Shaders/Transparent/Diffuse"}
    slots = {"_MainTex", "_BumpMap", "_MetallicGlossMap", "_ParallaxMap", "_OcclusionMap", "_EmissionMap"}
    for item in inventory["textures"]:
        row = dict(item)
        row["file"] = normalized(item["file"])
        key = row["file"], row["path_id"]
        target = targets.get(key)
        if target:
            mips = target["target_mip_bytes"]
            size = target["target_bytes"]
            row["targetFormat"] = target["target_name"]
        else:
            bpp = {1: 1, 2: 2, 3: 3, 4: 4, 5: 4, 17: 8}.get(item["m_TextureFormat"])
            mips = [max(1, item["m_Width"] >> n) * max(1, item["m_Height"] >> n) * bpp for n in range(item["m_MipCount"])] if bpp else []
            size = item["m_StreamData"]["size"] or item.get("inline_bytes") or item["m_CompleteImageSize"] * item.get("m_ImageCount", 1)
            row["targetFormat"] = item["format"]
        row["estimatedResidentPayloadBytes"] = size
        row["mipBytesPerImage"] = mips
        row["materialUses"] = usage.get(key, [])
        row["externalNonReadableMipped2D"] = bool(item["kind"] == "Texture2D" and not item["m_IsReadable"] and item["m_StreamData"]["size"] and item["m_MipCount"] > 1)
        row["stockUv0MaterialCandidate"] = bool(row["externalNonReadableMipped2D"] and row["materialUses"] and all(u["shader"] in stock and u["property"] in slots for u in row["materialUses"]))
        # This is material eligibility evidence, not permission to enable blindly:
        # non-material UI/compute/decal uses and shader UV scaling must also be audited.
        row["allMaterialShaders"] = sorted({u["shader"] for u in row["materialUses"]})
        group = groups[row["file"]]
        group["textures"] += 1
        group["residentPayloadBytes"] += size
        if item["m_IsReadable"]:
            group["readableTextures"] += 1
            group["readablePayloadBytes"] += size
        if item["m_MipCount"] > 1:
            group["mippedTextures"] += 1
            group["mippedPayloadBytes"] += size
        if item["m_StreamingMipmaps"]:
            group["sourceStreamingTextures"] += 1
        for category in ("externalNonReadableMipped2D", "stockUv0MaterialCandidate"):
            if row[category]:
                group[category + "Count"] += 1
                group[category + "Bytes"] += size
                for n in (1, 2, 3):
                    group[category + "TailFromMip" + str(n)] += sum(mips[n:]) * item.get("m_ImageCount", 1)
        textures.append(row)

    meshes = []
    for filename in ("resources.assets", "sharedassets0.assets", "sharedassets1.assets"):
        env = UnityPy.load(str(SOURCE / filename))
        for obj in env.objects:
            if obj.type.name != "Mesh":
                continue
            tree = obj.read_typetree()
            metrics = [tree.get("m_MeshMetrics[0]"), tree.get("m_MeshMetrics[1]")]
            meshes.append({"file": filename, "pathId": obj.path_id, "name": tree["m_Name"], "uvMetrics": metrics,
                "uv0MetricFinitePositive": bool(metrics[0] is not None and math.isfinite(metrics[0]) and metrics[0] > 0),
                "isReadable": tree["m_IsReadable"], "keepVertices": tree["m_KeepVertices"], "keepIndices": tree["m_KeepIndices"]})
    result = {"inventorySha256": sha(inventory_path), "conversionPlanSha256": sha(plan_path),
        "groups": dict(groups), "textures": textures, "meshes": meshes,
        "limits": ["Payload arithmetic is not a device RSS/VAS measurement; driver allocations, copies, buffers and shaders are additional",
            "Texture inventory includes future/unloaded assets, so total is not proof all are simultaneously resident",
            "Stock UV0 material candidate excludes other shader families but still requires non-material-use audit and runtime calculated-mip verification",
            "No source textures, dimensions, mip payloads, flags or canonical staging were changed"]}
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "inventory.json").write_text(json.dumps(result, indent=2))
    summary = {"textureCount": len(textures), "groups": dict(groups), "meshCount": len(meshes),
        "meshUv0FinitePositive": sum(m["uv0MetricFinitePositive"] for m in meshes),
        "sourceStreaming": [(t["file"], t["path_id"], t["m_Name"]) for t in textures if t["m_StreamingMipmaps"]]}
    (OUT / "summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
