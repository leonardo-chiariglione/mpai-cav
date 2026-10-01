# THE MMM-TEC V2.2 API AS OPENAPI 3.1, BUILT FROM THE SCHEMAS. Every schema the API
# refers to - the four of the API (MMM-API chapter, 3), every MMM V2.2 data schema, and
# whatever they refer to in OSD, PTF, TFA, AIF and the other standards - becomes a
# component, its $refs pointing at components. The schemas are not changed: the API
# is made non-recursive here, by two rules.
#
#  1. METADATA HAS NO METADATA. A Data Type reached inside a Data Exchange Metadata
#     (its Annotations, their Space and Rights, its Security...) is taken without its
#     own DataXMData. That is what makes DEM -> Rights -> DEM a loop; this cuts it.
#  2. BELOW THE TOP, AN IDENTIFIER. Where a schema refers back to one it is part of
#     (a Text Object whose sub-objects are Text Objects, an Event made of Events), the
#     inner reference takes the identifier: the Item is then read by its ID, at
#     GET /items/{itemID}. Where the reference was one branch of "X or its ID", the
#     two branches become one.
#
# Faults of the published schemas - a $ref to nothing - are corrected here, and each
# correction is reported (FIXES). Run from the repository root:
#
#   python MMM/Api/build_openapi.py          writes MMM/Api/MMM-API.json and a report
import json, os, re, sys
from collections import OrderedDict

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
SCHEMAS = os.path.normpath(os.path.join(ROOT, "schemas"))
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "MMM-API.json")
BASE = "https://schemas.mpai.community/"
DEM = "PTF/V1.0/data/DataExchangeMetadata.json"

# $refs of the published schemas that name nothing, and what they mean (none at present:
# the faults found when this was written were corrected in the schemas).
FIXES = {}
DROP = ("$schema", "$id", "$anchor", "$comment")

docs, used_fixes, cuts, dem_drops, problems = {}, set(), [], set(), []

def load(rel):
    if rel not in docs:
        p = os.path.join(SCHEMAS, *rel.split("/"))
        docs[rel] = json.load(open(p, encoding="utf-8-sig"), object_pairs_hook=OrderedDict) if os.path.exists(p) else None
    return docs[rel]

def target(ref, here):
    url, _, frag = ref.partition("#")
    if not url: rel = here
    elif url.startswith(BASE): rel = url[len(BASE):]
    else: rel = os.path.normpath(os.path.join(os.path.dirname(here), url)).replace("\\", "/")
    key = (rel, frag)
    if key in FIXES: used_fixes.add(key); return FIXES[key]
    if (rel, "") in FIXES and frag: used_fixes.add((rel, "")); return (FIXES[(rel, "")][0], frag)
    return key

def node(rel, frag):
    doc = load(rel)
    if doc is None: return None
    n = doc
    for part in [p for p in frag.split("/") if p]:
        part = part.replace("~1", "/").replace("~0", "~")
        if not isinstance(n, dict) or part not in n: return None
        n = n[part]
    return n

def cname(rel, frag, in_dem):
    std, ver, _, f = rel.split("/")[:4] if rel.count("/") >= 3 else (rel, "", "", "")
    name = f"{std}_{ver.replace('.', '')}_{os.path.splitext(f)[0]}"
    if frag: name += "__" + frag.split("/")[-1]
    return re.sub(r"[^A-Za-z0-9_.-]", "_", name) + ("_InDEM" if in_dem else "")

components, building = OrderedDict(), []

def component(rel, frag, in_dem):
    """The component of (rel, frag) - built once - or None when it is on the way (a cycle)."""
    name = cname(rel, frag, in_dem)
    if name in components: return name
    if name in building: return None
    n = node(rel, frag)
    if n is None:
        problems.append(f"unresolved: {rel}#{frag}"); components[name] = {"description": f"Unresolved: {rel}#{frag}"}; return name
    building.append(name)
    body = translate(n, rel, in_dem or rel == DEM)
    if isinstance(body, dict) and not frag:
        body = OrderedDict([("title", n.get("title", name))] + [(k, v) for k, v in body.items() if k != "title"])
    building.pop()
    components[name] = body
    return name

def ident(rel, frag):
    what = (frag.split("/")[-1] if frag else os.path.splitext(rel.split("/")[-1])[0])
    return OrderedDict([("type", "string"), ("description", f"Identifier of the {what}: the Item is read at GET /items/{{itemID}}.")])

def translate(n, here, in_dem):
    if isinstance(n, list): return [translate(x, here, in_dem) for x in n]
    if not isinstance(n, dict): return n
    if isinstance(n.get("$ref"), str):
        rel, frag = target(n["$ref"], here)
        name = component(rel, frag, in_dem)
        if name is None:
            cuts.append(f"{building[-1]} -> {cname(rel, frag, in_dem)}")
            return ident(rel, frag)
        out = OrderedDict([("$ref", f"#/components/schemas/{name}")])
        out.update((k, translate(v, here, in_dem)) for k, v in n.items() if k != "$ref" and k not in DROP)
        return out
    out = OrderedDict()
    for k, v in n.items():
        if k in DROP or k in ("$defs", "definitions"): continue
        if k == "properties" and isinstance(v, dict):
            props = OrderedDict()
            for p, s in v.items():
                if in_dem and isinstance(s, dict) and isinstance(s.get("$ref"), str) and target(s["$ref"], here) == (DEM, ""):
                    dem_drops.add(here); continue                       # rule 1
                props[p] = translate(s, here, in_dem)
            out[k] = props
            continue
        out[k] = translate(v, here, in_dem)
    if in_dem and "required" in out and "properties" in out:
        out["required"] = [r for r in out["required"] if r in out["properties"] or r not in ("DataXMData",)]
    for key in ("oneOf", "anyOf"):                                       # rule 2: two branches become one
        if isinstance(out.get(key), list):
            seen, keep = [], []
            for b in out[key]:
                s = json.dumps(b, sort_keys=True)
                if s not in seen: seen.append(s); keep.append(b)
            if len(keep) == 1 and len(out) == 1: return keep[0]
            out[key] = keep
    return out

# ---------------------------------------------------------------- the API's own schemas
ACTIONS = OrderedDict([
    ("communicate", [("mm-send", "MM-Send"), ("resolve", "Resolve")]),
    ("economy", [("license", "License"), ("post", "Post"), ("transact", "Transact")]),
    ("execute", [("execute", "Execute")]),
    ("export", [("mu-actuate", "MU-Actuate"), ("mu-add", "MU-Add"), ("mu-animate", "MU-Animate"), ("mu-move", "MU-Move"), ("mu-send", "MU-Send")]),
    ("identity", [("hide", "Hide"), ("identify", "Identify"), ("modify", "Modify"), ("register", "Register")]),
    ("import", [("um-actuate", "UM-Actuate"), ("um-capture", "UM-Capture"), ("um-send", "UM-Send")]),
    ("information", [("authenticate", "Authenticate"), ("discover", "Discover"), ("interpret", "Interpret")]),
    ("item", [("author", "Author"), ("convert", "Convert")]),
    ("locate", [("mm-add", "MM-Add"), ("mm-animate", "MM-Animate"), ("mm-capture", "MM-Capture"), ("mm-move", "MM-Move"), ("property-change", "Property Change")]),
    ("rights", [("rights-change", "Rights Change"), ("validate", "Validate")]),
])
SERVICES = {"communicate": "Communicate", "economy": "Economy", "execute": "Execute", "export": "Export", "identity": "Identity Manage",
            "import": "Import", "information": "Information", "item": "Item Manage", "locate": "Locate", "rights": "Rights Manage"}
BASELINE = {"MM-Send", "MU-Actuate", "Identify", "UM-Actuate", "UM-Capture", "MM-Add", "MM-Animate", "MM-Move"}
ALL = [a for v in ACTIONS.values() for _, a in v]
R = lambda name: {"$ref": f"#/components/schemas/{name}"}

api = OrderedDict()
api["StatusedRef"] = {"type": "object", "additionalProperties": False, "required": ["itemID", "status"],
    "description": "A Rights, Transaction or Service Pricing Model Item and its status: Model in a Request, Final in a Response.",
    "properties": {"itemID": {"type": "string"}, "status": {"type": "string", "enum": ["Model", "Final"]}}}
api["Complement"] = {"description": "An Item or a Process: by its identifier, as a Statused Reference, or the Item itself.",
    "oneOf": [{"type": "string"}, R("StatusedRef"), R("MMM4_V22_AnyItem")]}
api["Complements"] = {"type": "object", "additionalProperties": False,
    "description": "The Complements of a Process Action, in the order given by its Backus Naur Form.",
    "properties": {k: {"type": "string"} for k in ("Nil", "At", "From", "To", "Of")} | {"With": {"type": "array", "items": R("Complement")}}}
api["ProcessActionRequest"] = {"type": "object", "additionalProperties": False, "required": ["sourceProcessID", "deonticVerb", "action", "complements"],
    "properties": {"sourceProcessID": {"type": "string"}, "deonticVerb": {"type": "string", "enum": ["May", "May Not", "Must"]},
                   "action": {"type": "string", "enum": ALL}, "complements": R("Complements")}}
api["PAStatus"] = {"type": "object", "additionalProperties": False, "required": ["code"],
    "properties": {"code": {"type": "string", "description": "Ack, or the error of the Process Action (e.g. Clash, FaultyPA, IncID, MLocOOR, InsRights, InsValue)."},
                   "detail": {"type": "string"}}}
api["ProcessActionResponse"] = {"type": "object", "additionalProperties": False, "required": ["destinationProcessID", "paStatus"],
    "properties": {"destinationProcessID": {"type": "string"}, "complements": R("Complements"), "paStatus": R("PAStatus")}}
api["PaymentRequired"] = {"type": "object", "additionalProperties": False, "required": ["servicePricingModel", "transaction"],
    "properties": {"servicePricingModel": R("StatusedRef"), "transaction": R("StatusedRef")}}
api["ItemSummary"] = {"type": "object", "additionalProperties": False, "required": ["itemID", "dataType"],
    "properties": {"itemID": {"type": "string"}, "dataType": {"type": "string", "description": "The Header of the Item, e.g. MMM-PRC-V2.2."}}}

# Every MMM V2.2 data schema is a component, whether a path names it or not.
roots = sorted(f for f in os.listdir(os.path.join(SCHEMAS, "MMM4", "V2.2", "data")) if f.endswith(".json"))
for f in roots: component(f"MMM4/V2.2/data/{f}", "", False)
for k, v in api.items(): components[k] = v

def pa_responses():
    j = lambda d, s: {"description": d, "content": {"application/json": {"schema": R(s)}}}
    return OrderedDict([("200", j("Performed: PA Status Ack.", "ProcessActionResponse")),
        ("201", j("Performed, and a new Item minted.", "ProcessActionResponse")),
        ("402", j("A pay Service: the Transaction at Status=Model is to be settled, then the Request resubmitted with it at Status=Final.", "PaymentRequired")),
        ("403", j("The requesting Process does not hold the Rights required.", "ProcessActionResponse")),
        ("404", j("Unknown Item or Process.", "ProcessActionResponse")),
        ("409", j("Conflicts with the current state of the Item.", "ProcessActionResponse")),
        ("415", j("The destination Process does not support the Item's Qualifier.", "ProcessActionResponse")),
        ("422", j("Well formed, but the Process Action could not be performed.", "ProcessActionResponse"))])

paths = OrderedDict()
for service, pas in ACTIONS.items():
    for seg, action in pas:
        paths[f"/api/v2.2/{service}/{seg}"] = {"post": OrderedDict([
            ("tags", [SERVICES[service]]), ("operationId", re.sub(r"[^A-Za-z]", "", action)),
            ("summary", action + (" (Baseline Profile)" if action in BASELINE else "")),
            ("parameters", [{"$ref": "#/components/parameters/IdempotencyKey"}]),
            ("requestBody", {"required": True, "content": {"application/json": {"schema": R("ProcessActionRequest")}}}),
            ("responses", pa_responses())])}
get = lambda summary, schema, params=(): {"get": OrderedDict([("tags", ["Items"]), ("summary", summary),
    ("parameters", [{"name": p, "in": "path", "required": True, "schema": {"type": "string"}} for p in params]),
    ("responses", {"200": {"description": "OK", "content": {"application/json": {"schema": schema}}}, "404": {"description": "Unknown Item or Process."}})])}
paths["/api/v2.2/items"] = get("List Items, optionally filtered by Data Type acronym.", {"type": "array", "items": R("ItemSummary")})
paths["/api/v2.2/items"]["get"]["parameters"] = [{"name": "dataType", "in": "query", "required": False, "schema": {"type": "string"}}]
paths["/api/v2.2/items/{itemID}"] = get("An Item, subject to the Rights of the calling Process.", R("MMM4_V22_AnyItem"), ["itemID"])
paths["/api/v2.2/items/{itemID}/rights"] = get("Item Capabilities: the Processes holding Rights on the Item.", {"type": "array", "items": R("MMM4_V22_Rights")}, ["itemID"])
paths["/api/v2.2/processes/{processID}/rights"] = get("The Rights held by a Process.", {"type": "array", "items": R("MMM4_V22_Rights")}, ["processID"])
paths["/api/v2.2/processes/{processID}/capabilities"] = get("Process Capabilities.", R("MMM4_V22_Capabilities"), ["processID"])
paths["/api/v2.2/m-instance/capabilities"] = get("M-Instance Capabilities: Profile, Actions, Items, Qualifiers, Rules.", R("MMM4_V22_Capabilities"))

doc = OrderedDict([
    ("openapi", "3.1.0"),
    ("info", {"title": "MPAI-MMM API", "version": "2.2",
              "description": "The API of MMM-TEC V2.2: one POST per Process Action, read-only Items and Capabilities. Built from the MMM-TEC V2.2 JSON Schemas by MMM/Api/build_openapi.py; its component schemas contain no recursion."}),
    ("servers", [{"url": "https://localhost:7099"}]),
    ("security", [{"bearer": []}]),
    ("paths", paths),
    ("components", OrderedDict([
        ("securitySchemes", {"bearer": {"type": "http", "scheme": "bearer", "description": "A token bound to the calling ProcessID."}}),
        ("parameters", {"IdempotencyKey": {"name": "Idempotency-Key", "in": "header", "required": True, "schema": {"type": "string"},
                        "description": "A retried Request is not performed twice: replaying a key returns the original Response."}}),
        ("schemas", OrderedDict(sorted(components.items())))]))])

# required properties that are not properties: reported, as faults of the schemas
for name, s in components.items():
    if isinstance(s, dict) and isinstance(s.get("properties"), dict) and isinstance(s.get("required"), list):
        for r in s["required"]:
            if r not in s["properties"] and s.get("additionalProperties") is False:
                problems.append(f"{name}: requires '{r}', which is not one of its properties")

json.dump(doc, open(OUT, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
report = [f"MMM-API.json: {len(paths)} paths, {len(components)} component schemas",
          f"rule 1 (no DataXMData inside a DEM) applied in {len(dem_drops)} schemas",
          f"rule 2 (an identifier below the top) applied at {len(cuts)} references:"] + [f"  {c}" for c in cuts] + \
         [f"corrected $refs of the schemas ({len(used_fixes)}):"] + [f"  {a}#{b} -> {FIXES[(a, b)][0]}#{FIXES[(a, b)][1]}" for a, b in sorted(used_fixes)] + \
         [f"faults reported ({len(problems)}):"] + [f"  {p}" for p in problems]
open(os.path.join(os.path.dirname(OUT), "MMM-API-report.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(report) + "\n")
print("\n".join(report))
