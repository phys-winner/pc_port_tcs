from pathlib import Path
import hashlib
import json

root = Path(__file__).resolve().parents[2]
source = Path(__file__).with_name("ui_routes.json")
raw = source.read_bytes()
assert hashlib.sha256(raw).hexdigest() == "813d100a8dff6965277c028a7fa70c2dc920c10468f4cb04e68b1cd769c6ca7a"
routes = json.loads(raw)["routes"]
assert len(routes) == 12 and sum(len(r["originalOnClick"]) for r in routes) == 17
lines = ["// Generated from the audited twelve original Back button routes.", "using System.Collections.Generic;", "namespace ClosingShift.Android {", "    internal static class AndroidUiRoutes {",
         "        internal sealed class Entry { internal string ScopeIdentity; internal int Priority; }",
         "        internal static readonly Dictionary<string, Entry> Back = new Dictionary<string, Entry> {"]
for row in routes:
    prefix = row["file"][5:] + ":"
    key = prefix + "\x1f".join(row["buttonPathNames"])
    scope = prefix + "\x1f".join(row["scope"]["pathNames"])
    lines.append("            { " + json.dumps(key) + ", new Entry { ScopeIdentity = " + json.dumps(scope) + ", Priority = " + str(row["priority"]) + " } },")
lines += ["        };", "    }", "}"]
(Path(__file__).parent / "AndroidUiRoutes.cs").write_text("\n".join(lines) + "\n", encoding="utf-8")
print("UI_ROUTES_GENERATED 12 buttons, 17 original ordered calls")
