"""Strict, round-trippable parser for the game's embedded I2 LanguageSourceAsset."""
from __future__ import annotations

import copy
import struct


def require(value, message):
    if not value:
        raise ValueError(message)


class Reader:
    def __init__(self, raw, offset=0):
        self.raw, self.p = raw, offset

    def take(self, size):
        require(0 <= size <= len(self.raw) - self.p, "I2 read out of bounds")
        result = self.raw[self.p:self.p + size]
        self.p += size
        return result

    def align(self):
        padding = self.take((-self.p) % 4)
        require(not any(padding), "Nonzero I2 padding requires explicit preservation")

    def integer(self):
        return struct.unpack("<i", self.take(4))[0]

    def aligned_byte(self):
        result = self.take(1)[0]
        self.align()
        return result

    def string(self):
        result = self.take(self.integer()).decode("utf-8")
        self.align()
        return result

    def array(self, function):
        count = self.integer()
        require(0 <= count <= 100000, "Invalid I2 array count")
        return [function() for _ in range(count)]

    def byte_array(self):
        result = list(self.take(self.integer()))
        self.align()
        return result


class Writer:
    def __init__(self, header=b""):
        self.raw = bytearray(header)

    def align(self):
        self.raw.extend(b"\0" * ((-len(self.raw)) % 4))

    def integer(self, value):
        self.raw.extend(struct.pack("<i", value))

    def aligned_byte(self, value):
        self.raw.append(value)
        self.align()

    def string(self, value):
        data = value.encode("utf-8")
        self.integer(len(data))
        self.raw.extend(data)
        self.align()

    def array(self, values, function):
        self.integer(len(values))
        for value in values:
            function(value)

    def byte_array(self, values):
        self.integer(len(values))
        self.raw.extend(bytes(values))
        self.align()


def mono_header_end(raw):
    require(len(raw) >= 32, "Truncated MonoBehaviour header")
    size = struct.unpack_from("<i", raw, 28)[0]
    require(0 <= size <= len(raw) - 32, "Invalid MonoBehaviour name")
    return (32 + size + 3) & ~3


def parse(raw):
    start = mono_header_end(raw)
    r = Reader(raw, start)
    model = {"header": raw[:start]}
    for key in ("sceneAgreement", "pluginAgreement", "googleLiveSyncUpToDate"):
        model[key] = r.aligned_byte()
    model["terms"] = r.array(lambda: {
        "term": r.string(), "type": r.integer(), "languages": r.array(r.string),
        "flags": r.byte_array(), "touch": r.array(r.string)})
    model["caseInsensitive"] = r.aligned_byte()
    model["missingAction"] = r.integer()
    model["appNameTerm"] = r.string()
    model["languages"] = r.array(lambda: {"name": r.string(), "code": r.string(), "flags": r.aligned_byte()})
    model["ignoreDeviceLanguage"] = r.aligned_byte()
    model["allowUnload"] = r.integer()
    model["networkOffset"] = r.p
    model["google"] = [r.string() for _ in range(4)]
    model["frequencyOffset"] = r.p
    model["frequency"] = r.integer()
    model["editorFrequency"] = r.integer()
    model["sync"] = r.integer()
    model["delayBytes"] = r.take(4)
    count = r.integer()
    require(0 <= count <= 100000, "Invalid I2 asset reference count")
    model["assets"] = r.take(count * 12)
    require(r.p == len(raw), "Unknown fields remain after I2 source")
    model["networkTail"] = raw[model["networkOffset"]:]
    require(len({t["term"] for t in model["terms"]}) == len(model["terms"]), "Duplicate I2 terms")
    return model


def serialize(model):
    w = Writer(model["header"])
    for key in ("sceneAgreement", "pluginAgreement", "googleLiveSyncUpToDate"):
        w.aligned_byte(model[key])
    w.integer(len(model["terms"]))
    for term in model["terms"]:
        w.string(term["term"])
        w.integer(term["type"])
        w.array(term["languages"], w.string)
        w.byte_array(term["flags"])
        w.array(term["touch"], w.string)
    w.aligned_byte(model["caseInsensitive"])
    w.integer(model["missingAction"])
    w.string(model["appNameTerm"])
    w.integer(len(model["languages"]))
    for language in model["languages"]:
        w.string(language["name"])
        w.string(language["code"])
        w.aligned_byte(language["flags"])
    w.aligned_byte(model["ignoreDeviceLanguage"])
    w.integer(model["allowUnload"])
    # Preserve the entire network/offline and external-assets tail verbatim.
    w.raw.extend(model["networkTail"])
    return bytes(w.raw)


def add_russian(model, translated, supplemental):
    result = copy.deepcopy(model)
    require(len(model["languages"]) == 4, "Expected four original languages")
    require(set(translated) == {term["term"] for term in model["terms"]}, "Russian translation set differs from original terms")
    require(supplemental["language"] == {"code": "ru", "name": "Русский (AI)"}, "Russian language identity differs")
    result["languages"].append({"name": "Русский (AI)", "code": "ru", "flags": 0})
    for term in result["terms"]:
        require(len(term["languages"]) == len(term["flags"]) == 4 and not term["touch"], "Original I2 column dimensions differ")
        term["languages"].append(translated[term["term"]])
        term["flags"].append(2)  # I2 TermData.IsAutoTranslated bit, identifying AI text.
    seen = set(translated)
    additions = supplemental["terms"] + supplemental.get("newTerms", [])
    for term in additions:
        require(term["term"] not in seen, "Duplicate supplemental term")
        require(all(isinstance(term.get(key), str) for key in ("term", "en", "jp", "ru")), "Supplemental term lacks explicit source values")
        # Empty legacy locale cells use the game's own missing-language fallback.
        result["terms"].append({"term": term["term"], "type": term.get("type", 0),
                               "languages": [term["en"], term["jp"], "", "", term["ru"]],
                               "flags": [0, 0, 0, 0, 2], "touch": []})
        seen.add(term["term"])
    for row in supplemental["componentTermOverrides"]:
        require(row["replacementTerm"] in seen, "Missing per-component override translation")
    raw = serialize(result)
    check = parse(raw)
    require(check["networkTail"] == model["networkTail"], "I2 offline/network settings changed")
    require(check["frequency"] == 1, "I2 offline Google frequency must remain Never=1")
    require(check["languages"][:4] == model["languages"], "Original language descriptors changed")
    for old, new in zip(model["terms"], check["terms"]):
        require(old == {**new, "languages": new["languages"][:4], "flags": new["flags"][:4]}, "An original translation changed")
    require(serialize(check) == raw, "Russian I2 serialization is not stable")
    return raw, check
