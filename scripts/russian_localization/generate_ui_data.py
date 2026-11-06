"""Generate context-limited runtime presentation tables from audited RU UI references."""
from collections import defaultdict
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).with_name("data")


def literal(value):
    return json.dumps(value, ensure_ascii=False)


def key(row):
    filename = row["file"]
    if not filename.startswith("level") or not filename[5:].isdigit():
        return None
    names = [h["name"] for h in reversed(row["hierarchy"])] if "hierarchy" in row else row["gameObjectPath"].split("/")
    return filename[5:] + ":" + "\x1f".join(names)


def main():
    path = BASE / "unlocalized_ui_translations.json"
    translation = json.loads(path.read_text(encoding="utf-8"))
    dropdown_path = BASE / "dropdown_context.json"
    dropdowns = json.loads(dropdown_path.read_text(encoding="utf-8"))
    language_dropdown = next(row for row in dropdowns if row["file"] == "level0" and row["pathId"] == 4054)
    language_prefix = key(language_dropdown)
    owners = defaultdict(dict)
    for entry in translation["entries"]:
        for row in entry["references"]:
            identity = key(row)
            if identity is None or identity == language_prefix or identity.startswith(language_prefix + "\x1f"):
                continue
            previous = owners[identity].setdefault(entry["source"], entry["ru"])
            assert previous == entry["ru"], "Conflicting Russian text for identical owner and source"
    role_path = BASE / "credit_role_overrides.json"
    role = json.loads(role_path.read_text(encoding="utf-8")) if role_path.exists() else []
    role = role.get("entries", []) if isinstance(role, dict) else role
    for row in role:
        references = row.get("references", [row])
        for reference in references:
            identity = key(reference)
            if identity:
                owners[identity][row["source"]] = row["ru"]
    # The sole language dropdown has runtime-generated options and must remain I2-owned.
    dropdown_keys = sorted({key(row) for row in dropdowns if not (row["file"] == "level0" and row["pathId"] == 4054)})
    output = ROOT / "scripts/russian_localization/RussianUiData.cs"
    lines = ["// Generated from audited exact source strings and hierarchy references; do not edit.",
             "using System.Collections.Generic;", "namespace ClosingShift.Localization", "{", "    internal static class RussianUiData", "    {"]
    lines.append("        public static readonly Dictionary<string, string> Labels = new Dictionary<string, string> { ")
    for source, target in sorted(translation["sourceToRussian"].items()):
        lines.append("            { " + literal(source) + ", " + literal(target) + " },")
    lines.append("        };")
    lines.append("        public static readonly Dictionary<string, Dictionary<string, string>> Owners = new Dictionary<string, Dictionary<string, string>> {")
    for identity, labels in sorted(owners.items()):
        lines.append("            { " + literal(identity) + ", new Dictionary<string, string> {")
        for source, target in sorted(labels.items()):
            lines.append("                { " + literal(source) + ", " + literal(target) + " },")
        lines.append("            } },")
    lines.append("        };")
    lines.append("        public static readonly HashSet<string> DropdownOwners = new HashSet<string> {")
    lines.extend("            " + literal(identity) + "," for identity in dropdown_keys)
    lines.extend(["        };", "    }", "}", ""])
    output.write_text("\n".join(lines), encoding="utf-8")
    manifest = {"translation_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "dropdown_inventory_sha256": hashlib.sha256(dropdown_path.read_bytes()).hexdigest(),
                "credit_role_overrides_sha256": hashlib.sha256(role_path.read_bytes()).hexdigest() if role_path.exists() else None,
                "generated_source_sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
                "static_owner_paths": len(owners), "caption_source_strings": len(translation["sourceToRussian"]),
                "known_dropdown_owners": len(dropdown_keys), "i2_language_dropdown_excluded": "level0:4054",
                "i2_caption_template_subtree_excluded": True,
                "optiondata_modified": False}
    report = ROOT / "work/russian_localization/ui-data-manifest.json"
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
