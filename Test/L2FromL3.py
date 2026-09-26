# L2 FROM L3: when an L3 changes in what the Standard specifies, its L2 is derived
# from it - "reverse engineered". What the L3 decides for the Standard is taken from
# it; what only an implementation decides is left out; what an L3 cannot say (the
# Implementer's template, the options it chooses from) is kept from the L2 there is.
#
#   python Test\L2FromL3.py AIMs\AMDs\1MMC-HCI-V2.5-I01.json            (shows the L2)
#   python Test\L2FromL3.py AIMs\AMDs\1MMC-HCI-V2.5-I01.json --write    (writes it)
#
# An L2 is the TYPE - generic; an L3 is one implementation - specific. From the L3,
# only what specifies the type: Header and the AIM it names, ExternalPorts (Name,
# Direction, DataType, PortNumber, IsOptional, Input/Output groups, Technology),
# InternalTypes, SubAIMs (as the AIM types they instantiate) and the Topology
# (Sub-AIM instances as their types; names, Data Types and Port Numbers as in the
# L3). Left out: the implementation's own choices - OnDegraded, Execution, Record,
# RestartLimit, Period, Deadline, StorageControl; a Port's Depth, Overflow, MaxAge,
# Transport, AcceptedTransports, and its Protocol and IsRemote (generic in an L2:
# ""). From the existing L2 (else a template), never from the L3: Description,
# Implementations, ResourcePolicies, DataXMData, Documentation.
import json, os, re, sys, glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORT_FIELDS = ["Name", "Direction", "DataType", "PortNumber", "IsOptional", "Input", "Output", "Technology"]

TEMPLATE = {
    "Implementations": [{"BinaryName": "/* provided by implementer */", "Architecture": "/* x86-64 | ARM64 */",
                         "OperatingSystem": "/* Windows | Linux | MacOS */", "Version": "/* implementer version */",
                         "Source": "/* AIMStorage | MPAIStore */", "Destination": "/* provided by implementer */"}],
    "ResourcePolicies": [{"Name": "Memory", "Minimum": "/* GB */", "Maximum": "/* GB */", "Request": "/* GB */"},
                         {"Name": "CPU:Number", "Minimum": "/* integer, 0=all */", "Maximum": "/* integer, 0=all */", "Request": "/* integer */"},
                         {"Name": "CPU:Class", "Minimum": "/* Low | Medium | High */", "Maximum": "/* Low | Medium | High */", "Request": "/* Low | Medium | High */"},
                         {"Name": "GPU:Number", "Minimum": "/* integer, 0=all */", "Maximum": "/* integer, 0=all */", "Request": "/* integer */"}],
    "DataXMData": {},
}


def aim_type(instance):
    """1MMC-ASR-V2.5-I01 -> MMC-ASR-V2.5"""
    return re.sub(r"-I\d+$", "", re.sub(r"^[0-9A-Za-z]*?(?=[A-Z]{3}-[A-Z]{3}-V)", "", instance))


def existing_l2(header):
    for f in glob.glob(os.path.join(ROOT, "schemas", "**", "AIMs", "*.json"), recursive=True):
        try:
            d = json.load(open(f, encoding="utf-8-sig"))
        except Exception:
            continue
        if d.get("Header") == header:
            return f, d
    return None, None


def derive(l3, l2):
    header = l3.get("Header") or aim_type(l3["Identifier"]["AIMName"])
    out = {
        "Identifier": {"ImplementerID": "IIDRA-assigned", "ImplementationID": "/* String from Implementer */", "AIMName": header},
        "Header": header,
        "APIProfile": l3.get("APIProfile", "Basic"),
        "Description": (l2 or {}).get("Description") or f"This AIM performs {header}.",
    }
    if l3.get("InternalTypes"):
        out["InternalTypes"] = [{k: t[k] for k in ("Name", "DataType", "Output") if k in t} for t in l3["InternalTypes"]]
    ports = []
    for p in l3["ExternalPorts"]:
        q = {k: p[k] for k in PORT_FIELDS if k in p}
        q.setdefault("Technology", "Software")
        q["Protocol"] = ""
        q["IsRemote"] = ""
        ports.append(q)
    out["ExternalPorts"] = ports
    if l3.get("SubAIMs"):
        out["SubAIMs"] = [{"Identifier": {"ImplementerID": "IIDRA-assigned", "ImplementationID": "/* String from Implementer */",
                                          "AIMName": aim_type(s["Identifier"]["AIMName"]), "Relation": s["Identifier"].get("Relation", "")}}
                          for s in l3["SubAIMs"]]
        topology = []
        for line in l3.get("Topology", []):
            new = {}
            for side in ("Output", "Input"):
                end = dict(line[side])
                if end.get("AIMName"):
                    end["AIMName"] = aim_type(end["AIMName"])
                new[side] = end
            topology.append(new)
        out["Topology"] = topology
    for key in ("Implementations", "ResourcePolicies", "DataXMData", "Documentation"):
        if l2 and key in l2:
            out[key] = l2[key]
        elif key in TEMPLATE:
            out[key] = TEMPLATE[key]
    return out


def render(d):
    """One key per line; arrays of objects one object per line - as the L2s are written."""
    lines = ["{"]
    keys = list(d)
    for i, k in enumerate(keys):
        v = d[k]
        comma = "," if i < len(keys) - 1 else ""
        head = f'  "{k}":'.ljust(22)
        if isinstance(v, list) and v and all(isinstance(x, dict) for x in v):
            items = [json.dumps(x, ensure_ascii=False) for x in v]
            pad = " " * len(head)
            body = ("," + "\n" + pad + " ").join(items)
            lines.append(f"{head}[{body}]{comma}")
        else:
            lines.append(f"{head}{json.dumps(v, ensure_ascii=False)}{comma}")
        if i < len(keys) - 1:
            lines.append("")
    lines.append("}")
    return "\n".join(lines) + "\n"


def main():
    l3_path = sys.argv[1]
    l3 = json.load(open(l3_path, encoding="utf-8-sig"))
    header = l3.get("Header") or aim_type(l3["Identifier"]["AIMName"])
    path, l2 = existing_l2(header)
    text = render(derive(l3, l2))
    json.loads(text)
    if "--write" in sys.argv:
        if path is None:
            sys.exit(f"No L2 of {header} in schemas: say where it goes (none written).")
        crlf = "\r\n" in open(path, encoding="utf-8-sig", newline="").read()
        open(path, "w", encoding="utf-8", newline="").write(text.replace("\n", "\r\n") if crlf else text)
        print(f"written: {os.path.relpath(path, ROOT)}")
    else:
        print(text)


if __name__ == "__main__":
    main()
