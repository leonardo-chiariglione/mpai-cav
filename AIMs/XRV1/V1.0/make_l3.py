"""Writes the L3 AIM Instance Metadata of the four AIMs of XRV-LTP V1.0 that are software (ACG, PFI, PTI, OSI) into D:\\AI\\AIMs\\AMDs, each an instance of its L2
(D:\\AI\\schemas\\XRV1\\V1.0\\AIMs): the same Direction and Port Number, the same optional flags, Depth and Overflow; only what the Implementation supports.
Run: python -I make_l3.py
"""
import json, os

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMAS = os.path.join(HERE, "..", "..", "..", "schemas", "XRV1", "V1.0", "AIMs")
AMDS = os.path.join(HERE, "..", "..", "AMDs")

AIMS = {
    "ACG": ("ActionGeneration.json", "Acg", "action-generation"),
    "PFI": ("PerformanceStatusInterpretation.json", "Pfi", "performance-status-interpretation"),
    "PTI": ("ParticipantStatusInterpretation.json", "Pti", "participant-status-interpretation"),
    "OSI": ("OperatorStatusInterpretation.json", "Osi", "operator-status-interpretation"),
}


def port_line(p):
    keep = {"Name": p["Name"], "Direction": p["Direction"], "DataType": p["DataType"]}
    for k in ("IsOptional", "Depth", "Overflow", "PortNumber"):
        if k in p:
            keep[k] = p[k]
    keep.update({"Technology": "Software", "Protocol": "", "IsRemote": False})
    return "{" + ", ".join(f"{json.dumps(k)}: {json.dumps(v)}" for k, v in keep.items()) + "}"


for acr, (l2file, folder, slug) in AIMS.items():
    l2 = json.load(open(os.path.join(SCHEMAS, l2file), encoding="utf-8"))
    name = f"1XRV-{acr}-V1.0-I01"
    ports = ",\n                       ".join(port_line(p) for p in l2["ExternalPorts"])
    text = f'''{{ "Identifier":       {{"ImplementerID": "1", "ImplementationID": "XRV-{acr}-V1.0-I01", "AIMName": "{name}"}},
  "Header":           "XRV-{acr}-V1.0",
  "APIProfile":       "Basic",
  "Description":      {json.dumps(l2["Description"])},
  "ExternalPorts":    [{ports}],
  "SubAIMs":          [],
  "Topology":         [],
  "Implementations":  [{{"ImplementationURI": "https://github.com/leonardo-chiariglione/mpai-cav/tree/main/AIMs/XRV1/V1.0/{acr}", "MASPort": 443, "BinaryName": "Mpai.Xrv.{folder}", "Architecture": "x86-64", "OperatingSystem": "Windows", "Version": "1.0", "Source": "AIMStorage"}}],
  "ResourcePolicies": [{{"Name": "Memory", "Minimum": "0.5 GB", "Request": "1 GB", "Maximum": "4 GB"}},
                       {{"Name": "CPU:Class", "Minimum": "Low", "Request": "Low", "Maximum": "Medium"}}],
  "DataXMData":       {{}},
  "Documentation":    [{{"Type": "Specification", "URI": "https://mpai.community/standards/mpai-xrv/ltp/v1-0/ai-modules/{slug}/"}}] }}
'''
    json.loads(text)
    with open(os.path.join(AMDS, name + ".json"), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("written", name)
