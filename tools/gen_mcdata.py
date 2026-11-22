#!/usr/bin/env python3
"""Builds src/Mcs.Compiler/Data/mcdata.json from Mojang data generator reports.

Generate the reports for each version first:
  java -DbundlerMainClass=net.minecraft.data.Main -jar server-<ver>.jar --reports --output <gen>/<ver>/out
usage: gen_mcdata.py <gen dir>
<gen>/<ver>/disabled.txt (optional): IDs the server rejected with experiments off (feature-flagged content).
Produce it by running each ID through a real server (see tools/probe_ids.sh); those IDs are treated as unavailable.
Bit i in each mask = present in version group i (Versions.Groups order).
"""
import json, sys, os

# One representative jar per pack-format group, in Versions.Groups order
GROUPS = [("1.20.1", "1.20.1"), ("1.20.2", "1.20.2"), ("1.20.3", "1.20.4"), ("1.20.5", "1.20.6"),
          ("1.21", "1.21.1"), ("1.21.2", "1.21.3"), ("1.21.4", "1.21.4"), ("1.21.5", "1.21.5"),
          ("1.21.6", "1.21.6"), ("1.21.7", "1.21.8"), ("1.21.9", "1.21.10"), ("1.21.11", "1.21.11"),
          ("26.1", "26.1.2"), ("26.2", "26.2"), ("26.3", "26.3")]

def strip(k): return k.split(":", 1)[1]

gen = sys.argv[1]

blocks, items, entities, effects = {}, {}, {}, {}
experimental = {}
for i, (_, jar) in enumerate(GROUPS):
    rep = os.path.join(gen, jar, "out", "reports")
    bit = 1 << i
    dis = os.path.join(gen, jar, "disabled.txt")
    exp = set(open(dis).read().split()) if os.path.exists(dis) else set()
    experimental[GROUPS[i][0]] = exp
    for name, b in json.load(open(os.path.join(rep, "blocks.json"))).items():
        e = blocks.setdefault(strip(name), {"v": 0, "p": {}})
        if strip(name) in exp: continue
        e["v"] |= bit
        for prop, values in b.get("properties", {}).items():
            pe = e["p"].setdefault(prop, {"v": 0, "values": []})
            pe["v"] |= bit
            for val in values:
                if val not in pe["values"]: pe["values"].append(val)
    reg = json.load(open(os.path.join(rep, "registries.json")))
    for key, target in (("minecraft:item", items), ("minecraft:entity_type", entities), ("minecraft:mob_effect", effects)):
        for name in reg[key]["entries"]:
            target[strip(name)] = target.get(strip(name), 0) | (0 if strip(name) in exp else bit)

out = {"groups": [g for g, _ in GROUPS], "blocks": blocks, "items": items, "entities": entities, "effects": effects}
path = os.path.join(os.path.dirname(__file__), "..", "src", "Mcs.Compiler", "Data", "mcdata.json")
json.dump(out, open(path, "w"), separators=(",", ":"), sort_keys=True)
for g, e in experimental.items():
    hits = sorted(x for x in e if x in blocks or x in items or x in entities)
    if hits: print(f"experimental in {g}: {len(hits)}: {' '.join(hits[:12])}{' ...' if len(hits) > 12 else ''}")
print(f"blocks={len(blocks)} items={len(items)} entities={len(entities)} effects={len(effects)} -> {os.path.getsize(path)} bytes")
