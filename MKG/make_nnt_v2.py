"""Writes MPAI-NNW NNT V2.0: the Data Types and the L2s (AIMs and Modules) in the AIF V3.0 / M3194 form.

From NNW-NNT V1.1 (D:\\AI\\schemas\\NNW1\\V1.1): the AIM and AIW L2s of the AIF V2.0 form, whose ports are of raw types (uint8[],
bitstring).  The five Data Types of V2.0 are Payload, Parameters, Dataset, Inference and Count Error; a role (original or
retrieved payload; untrained, watermarked, modified or unwatermarked parameters; training or testing data) is a port number
or a port of a Module, not a Data Type.  The four AI Workflows are Modules: composite AIMs.

    python make_nnt_v2.py          writes D:\\DI\\schemas\\NNW1\\V2.0\\data and \\AIMs
"""
import json, os, re

DI = r'D:\DI\schemas'
OUT = os.path.join(DI, 'NNW1', 'V2.0')
VER = 'V2.0'
WEB = 'https://mpai.community/standards/mpai-nnw/nnt/v2-0/'
SIMPLE_TIME = 'https://schemas.mpai.community/OSD/V1.5/data/SimpleTime.json'
PTF_DXM = 'https://schemas.mpai.community/PTF/V1.0/data/DataExchangeMetadata.json'

def rd(p): return open(p, encoding='utf-8-sig', newline='').read()
gfd = rd(os.path.join(DI, 'PAF', 'V1.6', 'AIMs', 'GenerativeFaceDescription.json'))
TMPL = gfd[gfd.index('  "Implementations":'):gfd.index('  "DataXMData":')]

# ---- the Data Types
TYPES = [
    # file, acronym, title, ID field, data field, data schema, description
    ('Payload', 'PLD', 'Payload', 'PayloadID', 'PayloadData', {"type": "string", "pattern": "^[01]+$", "description": "The payload, a bitstring (V1.1: bitstring)."},
     'The payload that a watermark carries: a bitstring. The original payload (the one embedded) and the retrieved one (the one a decoder returns) are two ports of this Data Type.'),
    ('Parameters', 'PRM', 'Parameters', 'ParametersID', 'ParametersData', {"type": "string", "contentEncoding": "base64", "description": "The parameters of a neural network (V1.1: uint8[]), base64."},
     'The parameters of a neural network, of a Module: untrained, unwatermarked, watermarked or modified are roles of this Data Type, told apart by the port.'),
    ('Dataset', 'DST', 'Dataset', 'DatasetID', 'DatasetData', {"type": "string", "contentEncoding": "base64", "description": "The dataset (V1.1: uint8[]), base64."},
     'A dataset, for training or for testing a Module: the role is the port.'),
    ('Inference', 'INF', 'Inference', 'InferenceID', 'InferenceData', {"type": "string", "contentEncoding": "base64", "description": "What a Module infers (V1.1: uint8[]), base64."},
     'What a Module produces from the testing dataset: the inference of an unwatermarked, a watermarked or a modified Module.'),
    ('CountError', 'CER', 'Count Error', 'CountErrorID', 'CountErrorData', {"type": "integer", "minimum": 0, "description": "The number of bits of the retrieved payload that differ from the original (V1.1: uint8)."},
     'The count of the errors between the original and the retrieved payload, from which the Bit Error Rate follows.'),
]
os.makedirs(os.path.join(OUT, 'data'), exist_ok=True)
os.makedirs(os.path.join(OUT, 'AIMs'), exist_ok=True)
for fname, acr, title, idf, dataf, dschema, desc in TYPES:
    d = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": "https://schemas.mpai.community/NNW1/%s/data/%s.json" % (VER, fname),
        "title": "%s %s" % (title, VER),
        "description": desc,
        "type": "object", "additionalProperties": False,
        "required": ["Header", idf, dataf],
        "properties": {
            "Header": {"type": "string", "const": "NNW-%s-%s" % (acr, VER)},
            "MInstanceID": {"type": "string"}, "UEnvironmentID": {"type": "string"},
            idf: {"type": "string", "minLength": 1},
            idf.replace('ID', 'Time'): {"$ref": SIMPLE_TIME},
            dataf: dschema,
            "DataXMData": {"$ref": PTF_DXM},
            "DescrMetadata": {"type": "string", "maxLength": 2048}}}
    with open(os.path.join(OUT, 'data', fname + '.json'), 'w', encoding='utf-8', newline='') as f:
        f.write(json.dumps(d, indent=2, ensure_ascii=False) + '\n')

PLD, PRM, DST, INF, CER = ['NNW-%s-%s' % (a, VER) for a in ('PLD', 'PRM', 'DST', 'INF', 'CER')]
PORT = '"Technology": "Software", "Protocol": "", "IsRemote": false'

def port(name, direction, dt, number=None, optional=False):
    return ('{"Name": "%s", "Direction": "%s", "DataType": "%s", %s%s%s}' %
            (name, direction, dt, ('"PortNumber": %d, ' % number) if number else '', '"IsOptional": true, ' if optional else '', PORT))

ident = lambda n: '{"ImplementerID": "/* String assigned by IIDRA */", "ImplementationID": "/* String from Implementer */", "AIMName": "%s"}' % n
def sub_aim(n, number=None):
    return '{"Identifier": {"ImplementerID": "/* String assigned by IIDRA */", "ImplementationID": "/* String from Implementer */", "AIMName": "%s", "Relation": ""}%s}' % (n, (', "AIMNumber": %d' % number) if number else '')
def end(aim, dt, number=None, aimnum=None):
    r = {"AIMName": aim, "DataType": dt}
    if aimnum: r["AIMNumber"] = aimnum
    if number: r["PortNumber"] = number
    return r
def flow(o, i): return json.dumps({"Output": o, "Input": i})

def write(fname, acr, title, description, ports, subs=(), flows=()):
    name = 'NNW-%s-%s' % (acr, VER)
    pad = ' ' * 26
    s = ('{ "$schema":              "https://json-schema.org/draft/2020-12/schema",\n'
         '  "$comment":             "https://schemas.mpai.community/AIF/V3.0/data/AIMMetadata.json",\n'
         '  "$id":                  "https://schemas.mpai.community/NNW1/%s/AIMs/%s.json",\n'
         '  "title":                "%s %s",\n\n'
         '  "Identifier":           %s,\n'
         '  "Header":               "%s",\n\n'
         '  "APIProfile":           "Basic",\n'
         '  "Description":          %s,\n\n'
         '  "InternalTypes":        [ ],\n\n'
         '  "ExternalPorts":        [%s],\n\n'
         '  "SubAIMs":              [%s],\n\n'
         '  "Topology":             [%s],\n\n' %
         (VER, fname, title, VER, ident(name), name, json.dumps(description), (',\n' + pad).join(ports),
          (',\n' + pad).join(subs), (',\n' + pad).join(flows)))
    s += TMPL + '  "DataXMData":           {},\n  "Documentation":        [{"Type": "Specification", "URI": "%s"}] }\n' % WEB
    json.loads(re.sub(r'"(?:\\.|[^"\\])*"|/\*.*?\*/', lambda m: m.group(0) if m.group(0).startswith('"') else '', s, flags=re.S))
    with open(os.path.join(OUT, 'AIMs', fname + '.json'), 'w', encoding='utf-8', newline='') as f:
        f.write(s)

CMP, MFM, MDM, MTR, NWD, NWE, UWM, WMM, WWD, WWE = ['NNW-%s-%s' % (a, VER) for a in ('CMP', 'MFM', 'MDM', 'MTR', 'NWD', 'NWE', 'UWM', 'WMM', 'WWD', 'WWE')]
# ---- the AIMs
write('Comparator', 'CMP', 'Comparator', 'Comparator: compares the original payload with the retrieved one and gives the count of the errors, from which the Bit Error Rate follows.',
      [port('OriginalPayload', 'Input', PLD, 1), port('RetrievedPayload', 'Input', PLD, 2), port('CountError', 'Output', CER)])
write('ModificationModule', 'MFM', 'Modification Module', 'Modification Module: alters the parameters of a watermarked neural network (a modification: pruning, noise, quantisation) and gives the modified parameters.',
      [port('WatermarkedParameters', 'Input', PRM), port('ModifiedParameters', 'Output', PRM)])
write('ModifiedModule', 'MDM', 'Modified Module', 'Modified Module: the Module that has the modified parameters; it can be any AIM that produces an inference. It gives the inference of the modified network from the testing dataset.',
      [port('ModifiedParameters', 'Input', PRM), port('TestingDataset', 'Input', DST), port('ModifiedInference', 'Output', INF)])
write('ModuleTrainer', 'MTR', 'Module Trainer', 'Module Trainer: trains a Module with the training dataset and gives the (unwatermarked) parameters. The untrained parameters are those of the architecture of the Module, if none is given.',
      [port('UntrainedParameters', 'Input', PRM, optional=True), port('TrainingDataset', 'Input', DST), port('UnwatermarkedParameters', 'Output', PRM)])
write('NIRWatermarkDecoder', 'NWD', 'NIR Watermark Decoder', 'NIR Watermark Decoder: retrieves the payload inserted in the parameters of a Module (No Inference Robustness) and gives the retrieved payload.',
      [port('ModifiedParameters', 'Input', PRM), port('RetrievedPayload', 'Output', PLD)])
write('NTIWatermarkEmbedder', 'NWE', 'NTI Watermark Embedder', 'NTI Watermark Embedder: embeds the payload in the parameters of a Module without training it (No Training Imperceptibility) and gives the watermarked parameters.',
      [port('UnwatermarkedParameters', 'Input', PRM), port('Payload', 'Input', PLD), port('WatermarkedParameters', 'Output', PRM)])
write('UnwatermarkedModule', 'UWM', 'Unwatermarked Module', 'Unwatermarked Module: the Module without watermark; it can be any AIM that produces an inference. It gives the inference from the testing dataset.',
      [port('UnwatermarkedParameters', 'Input', PRM), port('TestingDataset', 'Input', DST), port('UnwatermarkedInference', 'Output', INF)])
write('WatermarkedModule', 'WMM', 'Watermarked Module', 'Watermarked Module: the Module that has the watermarked parameters; it can be any AIM that produces an inference. It gives the inference from the testing dataset.',
      [port('WatermarkedParameters', 'Input', PRM), port('TestingDataset', 'Input', DST), port('WatermarkedInference', 'Output', INF)])
write('WIRWatermarkDecoder', 'WWD', 'WIR Watermark Decoder', 'WIR Watermark Decoder: retrieves the payload from the inference of a Module (With Inference Robustness) and gives the retrieved payload.',
      [port('ModifiedInference', 'Input', INF), port('RetrievedPayload', 'Output', PLD)])
write('WTIWatermarkEmbedder', 'WWE', 'WTI Watermark Embedder', 'WTI Watermark Embedder: embeds the payload in the parameters of a Module while it trains it (With Training Imperceptibility) and gives the watermarked parameters.',
      [port('UntrainedParameters', 'Input', PRM, optional=True), port('TrainingDataset', 'Input', DST), port('Payload', 'Input', PLD), port('WatermarkedParameters', 'Output', PRM)])

# ---- the Modules (the AI Workflows of V1.1)
B = ''
write('NoInferenceRobustness', 'NIR', 'No Inference Robustness',
      'No Inference Robustness: the Module that evaluates the robustness of a watermark when the payload is retrieved from the parameters: it modifies the watermarked parameters, decodes the payload from the modified parameters and compares it with the original payload.',
      [port('OriginalPayload', 'Input', PLD), port('WatermarkedParameters', 'Input', PRM), port('RetrievedPayload', 'Output', PLD), port('CountError', 'Output', CER)],
      [sub_aim(x) for x in (MFM, NWD, CMP)],
      [flow(end(B, PLD), end(CMP, PLD, 1)), flow(end(B, PRM), end(MFM, PRM)), flow(end(MFM, PRM), end(NWD, PRM)),
       flow(end(NWD, PLD), end(CMP, PLD, 2)), flow(end(NWD, PLD), end(B, PLD)), flow(end(CMP, CER), end(B, CER))])
write('WithInferenceRobustness', 'WIR', 'With Inference Robustness',
      'With Inference Robustness: the Module that evaluates the robustness of a watermark when the payload is retrieved from the inference: it modifies the watermarked parameters, has the modified Module infer from the testing dataset, decodes the payload from the inference and compares it with the original payload.',
      [port('OriginalPayload', 'Input', PLD), port('TestingDataset', 'Input', DST), port('WatermarkedParameters', 'Input', PRM), port('RetrievedPayload', 'Output', PLD), port('CountError', 'Output', CER)],
      [sub_aim(x) for x in (MFM, MDM, WWD, CMP)],
      [flow(end(B, PLD), end(CMP, PLD, 1)), flow(end(B, DST), end(MDM, DST)), flow(end(B, PRM), end(MFM, PRM)), flow(end(MFM, PRM), end(MDM, PRM)),
       flow(end(MDM, INF), end(WWD, INF)), flow(end(WWD, PLD), end(CMP, PLD, 2)), flow(end(WWD, PLD), end(B, PLD)), flow(end(CMP, CER), end(B, CER))])
write('NoTrainingImperceptibility', 'NTI', 'No Training Imperceptibility',
      'No Training Imperceptibility: the Module that evaluates the imperceptibility of a watermark inserted without training: a Module Trainer trains the Module (1) and the Unwatermarked Module infers; a second training (2) gives the parameters into which the NTI Watermark Embedder embeds the payload, and the Watermarked Module infers. The two inferences are the outputs.',
      [port('TrainingDataset1', 'Input', DST, 1), port('TestingDataset1', 'Input', DST, 2), port('TrainingDataset2', 'Input', DST, 3), port('TestingDataset2', 'Input', DST, 4),
       port('Payload', 'Input', PLD), port('UnwatermarkedInference', 'Output', INF, 1), port('WatermarkedInference', 'Output', INF, 2)],
      [sub_aim(MTR, 1), sub_aim(MTR, 2), sub_aim(UWM), sub_aim(NWE), sub_aim(WMM)],
      [flow(end(B, DST, 1), end(MTR, DST, aimnum=1)), flow(end(MTR, PRM, aimnum=1), end(UWM, PRM)), flow(end(B, DST, 2), end(UWM, DST)), flow(end(UWM, INF), end(B, INF, 1)),
       flow(end(B, DST, 3), end(MTR, DST, aimnum=2)), flow(end(MTR, PRM, aimnum=2), end(NWE, PRM)), flow(end(B, PLD), end(NWE, PLD)),
       flow(end(NWE, PRM), end(WMM, PRM)), flow(end(B, DST, 4), end(WMM, DST)), flow(end(WMM, INF), end(B, INF, 2))])
write('WithTrainingImperceptibility', 'WTI', 'With Training Imperceptibility',
      'With Training Imperceptibility: the Module that evaluates the imperceptibility of a watermark inserted during training: a Module Trainer trains the Module (1) and the Unwatermarked Module infers; the WTI Watermark Embedder trains (2) and embeds the payload, and the Watermarked Module infers. The two inferences are the outputs.',
      [port('TrainingDataset1', 'Input', DST, 1), port('TestingDataset1', 'Input', DST, 2), port('TrainingDataset2', 'Input', DST, 3), port('TestingDataset2', 'Input', DST, 4),
       port('Payload', 'Input', PLD), port('UnwatermarkedInference', 'Output', INF, 1), port('WatermarkedInference', 'Output', INF, 2)],
      [sub_aim(MTR), sub_aim(UWM), sub_aim(WWE), sub_aim(WMM)],
      [flow(end(B, DST, 1), end(MTR, DST)), flow(end(MTR, PRM), end(UWM, PRM)), flow(end(B, DST, 2), end(UWM, DST)), flow(end(UWM, INF), end(B, INF, 1)),
       flow(end(B, DST, 3), end(WWE, DST)), flow(end(B, PLD), end(WWE, PLD)), flow(end(WWE, PRM), end(WMM, PRM)),
       flow(end(B, DST, 4), end(WMM, DST)), flow(end(WMM, INF), end(B, INF, 2))])
print('NNW-NNT V2.0 written to', OUT)
