"""Writes the L3 AIM Instance Metadata of the Live Theatrical Performance Composite AIM (1XRV-LTP-V1.0-I01) into D:\\AI\\AIMs\\AMDs, as an instance of its L2
(D:\\AI\\schemas\\XRV1\\V1.0\\AIMs\\LiveTheatricalPerformance.json): the same External Ports, the seven Sub-AIMs as the L3 Instances 1XRV-xxx-V1.0-I01, and the Topology of the L2.
The L2 writes its Topology by Port name; the L3 writes each end by Data Type, with the Port Number where the Port of that AIM has one (the schema of each AIM gives both).
Run: python -I make_ltp_l3.py
"""
import json, os

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMAS = os.path.join(HERE, "..", "..", "..", "schemas", "XRV1", "V1.0", "AIMs")
AMDS = os.path.join(HERE, "..", "..", "AMDs")

L2 = json.load(open(os.path.join(SCHEMAS, "LiveTheatricalPerformance.json"), encoding="utf-8"))
SUB = {"XRV-END-V1.0": "EnvironmentDescription.json", "XRV-PFI-V1.0": "PerformanceStatusInterpretation.json", "XRV-PTI-V1.0": "ParticipantStatusInterpretation.json",
       "XRV-OSI-V1.0": "OperatorStatusInterpretation.json", "XRV-ACG-V1.0": "ActionGeneration.json", "XRV-REG-V1.0": "REExperienceGeneration.json",
       "XRV-VEG-V1.0": "VEExperienceGeneration.json"}
# By (Name, Direction): an AIM may give an Input and an Output the same name (XRV-ACG: ParticipantData).
PORTS = {h: {(p["Name"], p["Direction"]): p for p in json.load(open(os.path.join(SCHEMAS, f), encoding="utf-8"))["ExternalPorts"]} for h, f in SUB.items()}
PORTS[""] = {(p["Name"], p["Direction"]): p for p in L2["ExternalPorts"]}


def instance(header):
    return "" if header == "" else "1" + header + "-I01"


def end(e, direction):
    aim = e["AIMName"]
    # a boundary Port seen from inside: its Direction is the other one (an Input of the Composite is an Output of the Topology)
    want = direction if aim else ("Input" if direction == "Output" else "Output")
    port = PORTS[aim][(e["PortName"], want)]
    out = {"AIMName": instance(aim), "DataType": port["DataType"]}
    if "PortNumber" in port:
        out["PortNumber"] = port["PortNumber"]
    return out


def port_line(p):
    keep = {"Name": p["Name"], "Direction": p["Direction"], "DataType": p["DataType"]}
    for k in ("IsOptional", "Depth", "Overflow", "PortNumber"):
        if k in p:
            keep[k] = p[k]
    keep.update({"Technology": "Software", "Protocol": "", "IsRemote": False})
    return "{" + ", ".join(f"{json.dumps(k)}: {json.dumps(v)}" for k, v in keep.items()) + "}"


def compact(o):
    return json.dumps(o, separators=(",", ":"))


ports = ",\n                       ".join(port_line(p) for p in L2["ExternalPorts"])
internal = ",\n                       ".join(compact(t) for t in L2["InternalTypes"])
subs = ",\n                       ".join(
    '{ "Identifier": { "ImplementerID": "1", "ImplementationID": "%s-I01", "AIMName": "%s", "Relation": "Internal" } }' % (s["Header"], instance(s["Header"]))
    for s in L2["SubAIMs"])
topology = ",\n                       ".join(compact({"Output": end(t["Output"], "Output"), "Input": end(t["Input"], "Input")}) for t in L2["Topology"])

text = f'''{{ "Identifier":       {{"ImplementerID": "1", "ImplementationID": "XRV-LTP-V1.0-I01", "AIMName": "1XRV-LTP-V1.0-I01"}},
  "Header":           "XRV-LTP-V1.0",
  "APIProfile":       "Basic",
  "Execution":        {json.dumps(L2["Execution"])},
  "OnDegraded":       {json.dumps(L2["OnDegraded"])},
  "Record":           {json.dumps(L2["Record"])},
  "Description":      {json.dumps(L2["Description"])},
  "InternalTypes":    [{internal}],
  "ExternalPorts":    [{ports}],
  "SubAIMs":          [{subs}],
  "Topology":         [{topology}],
  "Implementations":  [{{"ImplementationURI": "https://github.com/leonardo-chiariglione/mpai-cav/blob/main/AIMs/AMDs/1XRV-LTP-V1.0-I01.json", "MASPort": 443, "BinaryName": "Mpai.Xrv", "Architecture": "x86-64", "OperatingSystem": "Windows", "Version": "1.0", "Source": "AIMStorage"}}],
  "ResourcePolicies": [{{"Name": "Memory", "Minimum": "1 GB", "Request": "2 GB", "Maximum": "8 GB"}},
                       {{"Name": "CPU:Class", "Minimum": "Low", "Request": "Medium", "Maximum": "High"}}],
  "DataXMData":       {{}},
  "Documentation":    [{{"Type": "Specification", "URI": "https://mpai.community/standards/mpai-xrv/ltp/v1-0/ai-modules/live-theatrical-performance/"}}] }}
'''
json.loads(text)
with open(os.path.join(AMDS, "1XRV-LTP-V1.0-I01.json"), "w", encoding="utf-8", newline="\n") as f:
    f.write(text)
print("written 1XRV-LTP-V1.0-I01 with", len(L2["Topology"]), "connections")
