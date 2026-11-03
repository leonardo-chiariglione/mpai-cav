"""Synthetic newspaper pages from Wikinews texts (CC BY 2.5), with the exact text of each column as ground truth.
python gen_pages.py <wn.json> <out folder>
"""
import json, os, re, subprocess, sys, html, random
from PIL import Image, ImageFilter
import numpy as np

EDGE = r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
W, H = 1240, 1754                      # A4 at 150 dpi in CSS pixels; device scale factor 2 gives 300 dpi
NAMES = {'en': 'THE MPAI TEST GAZETTE', 'fr': 'LA GAZETTE DE TEST MPAI', 'de': 'DIE MPAI TESTZEITUNG', 'it': 'LA GAZZETTA DI PROVA MPAI',
         'es': 'LA GACETA DE PRUEBA MPAI', 'pt': 'A GAZETA DE TESTE MPAI'}
DATE = {'en': 'Saturday, 24 October 2026', 'fr': 'Samedi 24 octobre 2026', 'de': 'Samstag, 24. Oktober 2026', 'it': 'Sabato 24 ottobre 2026',
        'es': 'S\u00e1bado, 24 de octubre de 2026', 'pt': 'S\u00e1bado, 24 de outubro de 2026'}

def paragraphs(text):
    out = []
    for l in text.split('\n'):
        l = l.strip()
        if len(l) < 60 or l.startswith('='): continue
        out.append(l)
    return out

PAGE = """<!doctype html><html lang="%(lang)s"><head><meta charset="utf-8"><style>
html,body{margin:0;padding:0;background:#fff}
body{width:%(W)dpx;height:%(H)dpx;overflow:hidden;font-family:"Times New Roman",Times,serif;color:#111;position:relative}
#mast{position:absolute;left:70px;right:70px;top:46px;height:150px;text-align:center;border-bottom:4px double #111}
#mast h1{font-size:64px;margin:0;line-height:1.05;letter-spacing:2px;font-weight:bold}
#mast p{font-size:19px;margin:8px 0 0 0;border-top:1px solid #111;padding-top:6px}
.col{position:absolute;top:226px;height:1450px;width:340px;overflow:hidden;text-align:justify;font-size:%(fs)spx;line-height:1.27}
.col h2{font-size:27px;line-height:1.12;margin:0 0 10px 0;text-align:left;font-weight:bold}
.col p{margin:0 0 9px 0}
.col h2:not(:first-child){margin-top:26px}
.rule{position:absolute;top:226px;height:1450px;width:0;border-left:1px solid #555}
#result{display:none}
</style></head><body>
<div id="mast"><h1>%(name)s</h1><p>%(date)s</p></div>
<div class="col" id="c0" style="left:70px"></div><div class="rule" style="left:432px"></div>
<div class="col" id="c1" style="left:450px"></div><div class="rule" style="left:792px"></div>
<div class="col" id="c2" style="left:810px"></div>
<pre id="result"></pre>
<script>
const pool=%(pool)s;
let next=0; const result=[];
for(let c=0;c<3;c++){
  const col=document.getElementById('c'+c); const truth=[]; let full=false;
  while(!full && next<pool.length){
    const a=pool[next++]; const h=document.createElement('h2'); h.textContent=a.title; col.appendChild(h);
    if(col.scrollHeight>col.clientHeight){col.removeChild(h);full=true;next--;break;}
    let added=0; const mine=[a.title];
    for(const t of a.paras){
      const p=document.createElement('p'); p.textContent=t; col.appendChild(p);
      if(col.scrollHeight>col.clientHeight){col.removeChild(p);full=true;break;}
      mine.push(t); added++;
    }
    if(added==0){col.removeChild(h);next--;full=true;break;}
    truth.push(mine.join('\\n'));
    if(c<3 && full) break;
  }
  // the box of the text actually present, in CSS pixels
  const r=col.getBoundingClientRect(); const kids=[...col.children]; const last=kids[kids.length-1].getBoundingClientRect(); const first=kids[0].getBoundingClientRect();
  result.push({text:truth.join('\\n'),box:[Math.round(r.left),Math.round(first.top),Math.round(r.right),Math.round(last.bottom)],point:[Math.round(r.left+r.width/2),Math.round(first.top+first.height/2)],used:next});
}
document.getElementById('result').textContent=JSON.stringify(result);
fetch('http://127.0.0.1:8765/',{method:'POST',mode:'no-cors',body:JSON.stringify(result)});
</script></body></html>"""

def render(htmlpath, png, scale):
    subprocess.run([EDGE, '--headless=new', '--disable-gpu', '--user-data-dir=' + os.path.join(os.environ['TEMP'], 'edge-np'), f'--force-device-scale-factor={scale}', f'--window-size={W},{H}',
                    f'--screenshot={png}', 'file:///' + htmlpath.replace(os.sep, '/')], capture_output=True, timeout=120)

import threading, http.server
_got = {}
class _H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        _got['data'] = self.rfile.read(int(self.headers.get('Content-Length', 0))).decode('utf-8')
        self.send_response(200); self.send_header('Access-Control-Allow-Origin', '*'); self.end_headers()
    def do_OPTIONS(self):
        self.send_response(200); self.send_header('Access-Control-Allow-Origin', '*'); self.send_header('Access-Control-Allow-Methods', 'POST'); self.send_header('Access-Control-Allow-Headers', '*'); self.end_headers()
    def log_message(self, *a): pass
_srv = http.server.HTTPServer(('127.0.0.1', 8765), _H)
threading.Thread(target=_srv.serve_forever, daemon=True).start()

def render_and_layout(htmlpath, png, scale):
    """One run of the browser gives the picture and, posted by the page to this process, the layout."""
    _got.clear()
    render(htmlpath, png, scale)
    for _ in range(50):
        if 'data' in _got: break
        import time; time.sleep(0.1)
    import time
    for _ in range(300):
        if os.path.exists(png) and os.path.getsize(png) > 0: time.sleep(0.5); break
        time.sleep(0.1)
    return json.loads(_got['data'])

def degrade(src, dst, seed):
    """A page as a scanner or a camera gives it: half the resolution, a slight rotation, blur, noise, a grey paper, JPEG."""
    rnd = np.random.default_rng(seed)
    im = Image.open(src).convert('L')
    sc = 0.5
    im = im.resize((int(im.width * sc), int(im.height * sc)), Image.LANCZOS)
    angle = 1.1
    w, h = im.size
    rot = im.rotate(angle, resample=Image.BICUBIC, expand=False, fillcolor=235)
    rot = rot.filter(ImageFilter.GaussianBlur(0.7))
    a = np.asarray(rot).astype(np.float32)
    yy = np.linspace(0, 1, h)[:, None]
    a = a * (0.90 + 0.10 * (1 - 0.6 * yy))                  # uneven light, darker at the bottom
    a = a * 0.93 + 8                                          # grey paper
    a += rnd.normal(0, 7, a.shape)
    Image.fromarray(np.clip(a, 0, 255).astype(np.uint8)).save(dst, quality=40)
    return sc, angle, (w, h)

def transform(pt, sc, angle, size):
    """A point of the 300 dpi page, in the degraded one: scaled, then rotated by `angle` degrees counter-clockwise about the centre."""
    import math
    w, h = size
    x, y = pt[0] * sc, pt[1] * sc
    cx, cy = w / 2, h / 2
    t = math.radians(angle)
    dx, dy = x - cx, y - cy
    return [round(cx + dx * math.cos(t) + dy * math.sin(t), 1), round(cy - dx * math.sin(t) + dy * math.cos(t), 1)]

def main(wn, out, langs):
    data = json.load(open(wn, encoding='utf-8'))
    os.makedirs(out, exist_ok=True)
    tmp = os.path.join(out, '_tmp'); os.makedirs(tmp, exist_ok=True)
    index = []
    for lang in langs:
        arts = data[lang]
        pool = [{'title': a['title'], 'paras': paragraphs(a['text']), 'url': a['url'], 'revid': a.get('lastrevid')} for a in arts]
        pool = [a for a in pool if len(a['paras']) >= 3 and not re.search(r'Gaza|Isra|guerre|Ukra|Hamas|terror|attent|killed|dead|murder|shoot|Krieg|mort', a['title'] + ' ' + ' '.join(a['paras'][:2]), re.I)]
        js = json.dumps([{'title': a['title'], 'paras': a['paras']} for a in pool], ensure_ascii=False)
        page = PAGE % dict(lang=lang, W=W, H=H, fs=19.5, name=NAMES[lang], date=DATE[lang], pool=js)
        hp = os.path.abspath(os.path.join(tmp, f'{lang}.html'))
        open(hp, 'w', encoding='utf-8').write(page)
        folder = os.path.join(out, f'{lang}-clean'); os.makedirs(folder, exist_ok=True)
        cols = render_and_layout(hp, os.path.abspath(os.path.join(folder, 'page.png')), 2)
        # which articles went on the page: those whose title is in the text of a column
        onpage = [a for a in pool if any(a['title'] in c['text'] for c in cols)]
        meta = {'Id': f'{lang}-clean', 'Language': lang, 'Dpi': 300, 'Width': W * 2, 'Height': H * 2, 'Kind': 'synthetic, clean',
                'Columns': [{'Box': [v * 2 for v in c['box']], 'Point': [v * 2 for v in c['point']], 'Text': c['text']} for c in cols]}
        json.dump(meta, open(os.path.join(folder, 'columns.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
        json.dump([{'Title': a['title'], 'Url': a['url'], 'Revision': a['revid']} for a in onpage], open(os.path.join(folder, 'articles.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
        # the degraded copy
        dfolder = os.path.join(out, f'{lang}-degraded'); os.makedirs(dfolder, exist_ok=True)
        sc, angle, size = degrade(os.path.join(folder, 'page.png'), os.path.join(dfolder, 'page.jpg'), 1000 + len(index))
        dmeta = {'Id': f'{lang}-degraded', 'Language': lang, 'Dpi': 150, 'Width': size[0], 'Height': size[1], 'Kind': 'synthetic, degraded: 150 dpi, rotated 1.1 degrees, blurred, noisy, grey paper, JPEG quality 40',
                 'Columns': [{'Box': [round(v * sc) for v in c['Box']], 'Point': transform(c['Point'], sc, angle, size), 'Text': c['Text']} for c in meta['Columns']]}
        json.dump(dmeta, open(os.path.join(dfolder, 'columns.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
        json.dump([{'Title': a['title'], 'Url': a['url'], 'Revision': a['revid']} for a in onpage], open(os.path.join(dfolder, 'articles.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
        index.append((lang, [len(c['text']) for c in cols], len(onpage)))
        print(lang, [len(c['text']) for c in cols], len(onpage), 'articles')

if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], sys.argv[3].split(','))
