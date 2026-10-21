"""The web pages of six MMC V2.5 Composite AIMs (MCA, MTS, AMC, ACR, MAC, NPR), from their L2s (schemas/MMC/V2.5/AIMs), in the form of the other MMC V2.5 Composite AIM pages
(Answer to Multimodal Question as the model), and the figures of their Reference Models (SVG, then PNG).

  python Apps/MMC/Pages/mmc_pages.py <output folder>

Writes <folder>/<slug>.html (the content of the page, with the figure files named by the placeholders {FIG1} and {FIG2}), <folder>/<slug>-1.svg and -2.svg.
"""
import json, os, sys, html

W = 'https://mpai.community/standards/'
S = 'https://schemas.mpai.community/'
# Data Types: acronym -> (name, page)
DT = {
    'OSD-BSO': ('Basic Speech Object', W + 'mpai-osd/v1-5/data-types/basic-speech-object/'),
    'OSD-BTO': ('Basic Text Object', W + 'mpai-osd/v1-5/data-types/basic-text-object/'),
    'OSD-TXO': ('Text Object', W + 'mpai-osd/v1-5/data-types/text-object/'),
    'OSD-BVO': ('Basic Visual Object', W + 'mpai-osd/v1-5/data-types/basic-visual-object/'),
    'OSD-B3O': ('Basic 3D Model Object', W + 'mpai-osd/v1-5/data-types/basic-3d-model-object/'),
    'OSD-SEL': ('Selector', W + 'mpai-osd/v1-5/data-types/selector/'),
    'OSD-IID': ('Instance Identifier', W + 'mpai-osd/v1-5/data-types/instance-identifier/'),
    'OSD-PNT': ('Point', S + 'OSD/V1.5/data/Point.json'),
    'MMC-EPS': ('Personal Status', W + 'mpai-mmc/v2-5/data-types/personal-status/'),
    'MMC-SUM': ('Summary', W + 'mpai-mmc/v2-5/data-types/summary/'),
    'MMC-TPS': ('Text Personal Status', W + 'mpai-mmc/v2-5/data-types/text-personal-status/'),
    'PAF-FDO': ('Face Descriptors Object', W + 'mpai-paf/v1-6/data-types/face-descriptors-object/'),
    'PAF-PAV': ('Portable Avatar', W + 'mpai-paf/v1-6/data-types/portable-avatar/'),
}
# AIMs: acronym -> (name, page or None, JSON)
AIM = {
    'MMC-ASR': ('Automatic Speech Recognition', W + 'mpai-mmc/v2-5/ai-modules/automatic-speech-recognition/', S + 'MMC/V2.5/AIMs/AutomaticSpeechRecognition.json'),
    'MMC-EDP': ('Entity Dialogue Processing', W + 'mpai-mmc/v2-5/ai-modules/entity-dialogue-processing/', S + 'MMC/V2.5/AIMs/EntityDialogueProcessing.json'),
    'MMC-NLU': ('Natural Language Understanding', W + 'mpai-mmc/v2-5/ai-modules/natural-language-understanding/', S + 'MMC/V2.5/AIMs/NaturalLanguageUnderstanding.json'),
    'MMC-PSE': ('Personal Status Extraction', W + 'mpai-mmc/v2-5/ai-modules/personal-status-extraction/', S + 'MMC/V2.5/AIMs/PersonalStatusExtraction.json'),
    'MMC-ESD': ('Entity Speech Description', W + 'mpai-mmc/v2-5/ai-modules/entity-speech-description/', S + 'MMC/V2.5/AIMs/EntitySpeechDescription.json'),
    'MMC-SIR': ('Speaker Identity Recognition', W + 'mpai-mmc/v2-5/ai-modules/speaker-identity-recognition/', S + 'MMC/V2.5/AIMs/SpeakerIdentityRecognition.json'),
    'MMC-TTS': ('Text-to-Speech', W + 'mpai-mmc/v2-5/ai-modules/text-to-speech/', S + 'MMC/V2.5/AIMs/TextToSpeech.json'),
    'PAF-GFD': ('Generative Face Description', W + 'mpai-paf/v1-6/ai-modules/generative-face-description/', S + 'PAF/V1.6/AIMs/GenerativeFaceDescription.json'),
    'MMC-TTT': ('Text-to-Text Translation', W + 'mpai-mmc/v2-5/ai-modules/text-to-text-translation/', S + 'MMC/V2.5/AIMs/TextToTextTranslation.json'),
    'MMC-OCR': ('Optical Character Recognition', None, S + 'MMC/V2.5/AIMs/OpticalCharacterRecognition.json'),
    'MMC-CTS': ('Column Text Selection', None, S + 'MMC/V2.5/AIMs/ColumnTextSelection.json'),
    'PAF-RSR': ('Response and Scene Rendering', W + 'mpai-paf/v1-6/ai-modules/response-and-scene-rendering/', S + 'PAF/V1.6/AIMs/ResponseAndSceneRendering.json'),
    'PAF-PMX': ('Portable Avatar Multiplexing', W + 'mpai-paf/v1-6/ai-modules/portable-avatar-multiplexing/', S + 'PAF/V1.6/AIMs/PortableAvatarMultiplexing.json'),
    'PAF-EFD': ('Entity Face Description', None, S + 'PAF/V1.6/AIMs/EntityFaceDescription.json'),
    'PAF-FIR': ('Face Identity Recognition', W + 'mpai-paf/v1-6/ai-modules/face-identity-recognition/', S + 'PAF/V1.6/AIMs/FaceIdentityRecognition.json'),
    'OSD-IDR': ('ID Reconciliation', None, S + 'OSD/V1.5/AIMs/IDReconciliation.json'),
}
NB = '‑'   # the non-breaking hyphen of the acronyms on the other pages


def a(href, text): return f'<a href="{href}">{html.escape(text)}</a>'
def dt(acr, label=None): n, h = DT[acr]; return a(h, label or n)
def aimname(acr):
    n, page, js = AIM[acr]
    return a(page, n) if page else html.escape(n)
def acr(x): return x.replace('-', NB)


class Spec:
    def __init__(self, **k): self.__dict__.update(k)


def table(head, rows, widths=None):
    out = ['<table style="width: 100%;">\n<thead>\n<tr>' + ''.join(f'<th><strong>{h}</strong></th>' for h in head) + '</tr>\n</thead>\n<tbody>']
    for r in rows:
        out.append('<tr>' + ''.join(f'<td>{c}</td>' for c in r) + '</tr>')
    out.append('</tbody>\n</table>')
    return '\n'.join(out)


def io_table(inputs, outputs):
    rows = []
    out = ['<table style="width: 100%;">\n<thead>\n<tr><th><strong>Input</strong></th><th><strong>Description</strong></th></tr>\n</thead>\n<tbody>']
    for label, d, text in inputs: out.append(f'<tr><td>{dt(d, label)}</td><td>{text}</td></tr>')
    out.append('<tr><th><strong>Output</strong></th><th><strong>Description</strong></th></tr>')
    for label, d, text in outputs: out.append(f'<tr><td>{dt(d, label)}</td><td>{text}</td></tr>')
    out.append('</tbody>\n</table>')
    return '\n'.join(out)


def conf_table(inputs, outputs):
    out = ['<table style="width: 100%;">\n<tbody>']
    first = True
    for label, d, text in inputs:
        out.append(f'<tr><td>{"Receives" if first else ""}</td><td>{dt(d, label)}</td><td>{CONF[d]}</td></tr>'); first = False
    first = True
    for label, d, text in outputs:
        out.append(f'<tr><td>{"Produces" if first else ""}</td><td>{dt(d, label)}</td><td>{CONF[d]}</td></tr>'); first = False
    out.append('</tbody>\n</table>')
    return '\n'.join(out)


CONF = {
    'OSD-BSO': 'Shall validate against Basic Speech Object schema. Speech Data shall conform with Speech Qualifier.',
    'OSD-BTO': 'Shall validate against Basic Text Object schema. Text Data shall conform with Text Qualifier.',
    'OSD-TXO': 'Shall validate against Text Object schema. Text Data shall conform with Text Qualifier.',
    'OSD-BVO': 'Shall validate against Basic Visual Object schema. Visual Data shall conform with Visual Qualifier.',
    'OSD-B3O': 'Shall validate against Basic 3D Model Object schema. 3D Model Data shall conform with 3D Model Qualifier.',
    'OSD-SEL': 'Shall validate against Selector schema.',
    'OSD-IID': 'Shall validate against Instance Identifier schema.',
    'OSD-PNT': 'Shall validate against Point schema.',
    'MMC-EPS': 'Shall validate against Personal Status schema.',
    'MMC-SUM': 'Shall validate against Summary schema.',
    'MMC-TPS': 'Shall validate against Text Personal Status schema.',
    'PAF-PAV': 'Shall validate against Portable Avatar schema.',
}


def page(s):
    base = W + 'mpai-mmc/v2-5/ai-modules/'
    me = f'{s.name} ({acr(s.acr)})'
    L = [f'<p style="text-align: center;"><a href="{base}">Go to MPAI-MMC V2.5 AI Modules</a></p>',
         '<p style="display: flex; justify-content: space-between;">' + '\n'.join(f'<a href="{base}{s.slug}/#{x}">{t}</a>' for x, t in [('one', 'Function'), ('two', 'Ref. Model'), ('three', 'I/O Data'), ('four', 'SubAIMs'), ('five', 'JSON MData'),
                                                                                  ('six', 'Profiles'), ('seven', 'Ref. Software'), ('eight', 'Conformance'), ('nine', 'Performance')]) + '</p>',
         f'<h3 id="one">1 Functions</h3>\n{s.function}',
         f'<h3 id="two">2 Reference Model</h3>\nFigure 1 depicts the Reference Model of the {me} AIM.\n&nbsp;\n[FIG1]\n<p>Figure 1 – Reference Model of the {me} AIM</p>\nThe operation of the {me} AIM develops in the following way:\n<ol>\n'
         + '\n'.join(f' \t<li>{x}</li>' for x in s.steps) + '\n</ol>',
         f'<h3 id="three">3 I/O Data</h3>\nTable 1 specifies the Input and Output Data of the {me} AIM.\n<center>Table 1 – I/O Data of the {me} AIM</center>\n' + io_table(s.inputs, s.outputs),
         f'<h3 id="four">4 SubAIMs</h3>\n<h4>4.1 Reference Model</h4>\nFigure 2 depicts the Reference Model of the {me} Composite AIM.\n&nbsp;\n[FIG2]\n<p>Figure 2 – Reference Model of the {me} Composite AIM</p>',
         f'<h4>4.2 Operation</h4>\n{s.operation}',
         f'<h4>4.3 Functions of SubAIMs</h4>\nTable 2 provides the functions of the {me} SubAIMs.\n<center>Table 2 – Functions of the {me} SubAIMs</center>\n'
         + table(['SubAIM', 'Function'], [[aimname(x), f] for x, f, i, o in s.subs]),
         f'<h4>4.4 I/O Data of SubAIMs</h4>\nTable 3 gives the Input and Output Data of the {me} SubAIMs.\n<center>Table 3 – I/O Data of the {me} SubAIMs</center>\n'
         + table(['SubAIM', 'Input', 'Output'], [[aimname(x), '<br>'.join(i), '<br>'.join(o)] for x, f, i, o in s.subs]),
         f'<h4>4.5 AIMs and JSON Metadata</h4>\nTable 4 provides the links to the AIM specifications and JSON schemas. AIM1 indicates the Composite AIM and AIM2 its SubAIMs.\n<center>Table 4 – AIMs and JSON Metadata of the {me}</center>\n'
         + table(['AIM1', 'AIM2', 'Name', 'JSON'], [[acr(s.acr), '', a(base + s.slug + '/', s.name), a(S + 'MMC/V2.5/AIMs/' + s.schema, 'X')]]
                 + [['', acr(x), aimname(x), a(AIM[x][2], 'X')] for x, f, i, o in s.subs]),
         f'<h3 id="five">5 JSON Metadata</h3>\n<a href="{S}MMC/V2.5/AIMs/{s.schema}">{S}MMC/V2.5/AIMs/{s.schema}</a>',
         '<h3 id="six">6 Profiles</h3>\nNo Profiles.',
         '<h3 id="seven">7 Reference Software</h3>\nUnder development.',
         f'<h3 id="eight">8 Conformance Testing</h3>\nTable 5 provides the Conformance Testing Method for the {me} AIM. Conformance Testing of the individual SubAIMs is given by the individual AIM specifications.\n'
         'If a schema contains references to other schemas, conformance of data for the primary schema implies that any data referencing a secondary schema shall also validate against the relevant schema, if present, and conform with the Qualifier, if present.\n'
         f'<center>Table 5 – Conformance Testing Method for the {me} AIM</center>\n' + conf_table(s.inputs, s.outputs),
         '<h3 id="nine">9 Performance Assessment</h3>\nNot part of this specification.',
         f'<p style="text-align: center;"><a href="{base}">Go to MPAI-MMC V2.5 AI Modules</a></p>']
    return '\n'.join(L)


# ---------------------------------------------------------------- the figures (MPAI style: the Composite AIM in a frame, its SubAIMs green, arrows with the Data Types)
GREEN, FRAME = '#92d050', '#222'

def esc(t): return html.escape(t)

def text_lines(x, y, lines, size=13, anchor='middle', weight='normal', dy=15):
    out = []
    y0 = y - (len(lines) - 1) * dy / 2
    for k, t in enumerate(lines):
        out.append(f'<text x="{round(x, 1)}" y="{round(y0 + k * dy + 4, 1)}" font-size="{size}"' + (' font-weight="bold"' if weight == 'bold' else '') + f'>{esc(t)}</text>')
    return ''.join(out)

def arrow(pts, label=None, lx=None, ly=None, lines=None):
    d = 'M' + ' L'.join(f'{round(x, 1)},{round(y, 1)}' for x, y in pts)
    out = f'<path d="{d}" marker-end="url(#ah)"/>'
    if label or lines:
        ls = lines or [label]
        out += text_lines(lx, ly, ls, 12)
    return out

def svg(w, h, body):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}"><style>text{{font-family:Calibri,Arial,sans-serif;text-anchor:middle}}path{{fill:none;stroke:#222;stroke-width:1.2}}</style><rect width="100%" height="100%" fill="white"/>'
            '<defs><marker id="ah" markerWidth="9" markerHeight="9" refX="8" refY="4.5" orient="auto"><path d="M0,0 L9,4.5 L0,9 z" style="fill:#222;stroke:none"/></marker></defs>' + body + '</svg>')

def fig_aim(name, ac, ins, outs):
    """Figure 1: the AIM as one box, its Input and Output Data on its sides."""
    n = max(len(ins), len(outs)); h = max(150, 46 + n * 46); w = 620
    cx, top = w // 2, 20
    body = f'<rect x="185" y="{top}" width="250" height="{h - 40}" fill="{GREEN}" stroke="{FRAME}"/>' + text_lines(cx, top + (h - 40) / 2, [name, f'({acr(ac)})'], 14, weight='bold')
    for k, t in enumerate(ins):
        y = top + 28 + k * 46 if n > 1 else top + (h - 40) / 2
        y = top + (h - 40) * (k + 1) / (len(ins) + 1)
        body += arrow([(30, y), (185, y)], lines=[t], lx=108, ly=y - 10)
    for k, t in enumerate(outs):
        y = top + (h - 40) * (k + 1) / (len(outs) + 1)
        body += arrow([(435, y), (595, y)], lines=[t], lx=515, ly=y - 10)
    return svg(w, h, body)

def fig_composite(w, h, boxes, arrows_):
    """Figure 2: boxes = (id, x, y, w, h, [lines]); arrows_ = (points, [label lines], label x, label y). The frame is the Composite AIM; the Input and Output
    Data (the arrows that start at x<=20 or end at x>=w-10) and their labels are outside the frame, in the margins."""
    M = 130                                   # margin added on each side
    W2 = w + 2 * M
    body = f'<rect x="{60 + M}" y="10" width="{w - 120}" height="{h - 20}" fill="none" stroke="{FRAME}" stroke-width="1.5"/>'
    for _, x, y, bw, bh, lines in boxes:
        body += f'<rect x="{x + M}" y="{y}" width="{bw}" height="{bh}" fill="{GREEN}" stroke="#4a7a1a"/>' + text_lines(x + M + bw / 2, y + bh / 2, lines, 13)
    left = (10 + 60 + M) / 2
    right = (w - 60 + M + W2 - 10) / 2
    for a_ in arrows_:
        pts, lines, lx, ly = a_ if len(a_) == 4 else (a_[0], [], 0, 0)
        pts = [(x + M, y) for x, y in pts]
        if pts[0][0] <= 20 + M:
            pts[0] = (10, pts[0][1]); lx = left
        elif pts[-1][0] >= w - 10 + M:
            pts[-1] = (W2 - 10, pts[-1][1]); lx = right
        else:
            lx += M
        body += arrow(pts, lines=lines, lx=lx, ly=ly) if lines else arrow(pts)
    return svg(W2, h, body)
