"""Harvests the AI Modules and Data Types lists of the web pages of the standards listed in standards.json.

The web page of an AIM or of a Data Type is
    https://mpai.community/standards/mpai-<std>[/<part>]/v<x-y>/ai-modules/<slug>/        (and .../data-types/<slug>/)
where <part> is the acronym of the part of a multipart standard.  The home page of a standard lists its versions;
for each part this script takes the highest version, reads the two lists and writes weblinks.json:
    { "<STD>": { "<part or ''>": { "version": "V2.2", "base": "<url>", "ai-modules": { "<slug>": {"url", "name"} }, "data-types": { ... } } } }
Pages that do not exist are skipped.
"""
import html, json, os, re, urllib.request, urllib.error

ROOT = os.path.dirname(os.path.abspath(__file__))

def get(url):
    try:
        req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0 (MKG link harvester)'})
        return urllib.request.urlopen(req, timeout=30).read().decode('utf-8', 'ignore')
    except (urllib.error.HTTPError, urllib.error.URLError):
        return None

def vkey(v): return tuple(int(x) for x in re.findall(r'\d+', v))

table = json.load(open(os.path.join(ROOT, 'standards.json'), encoding='utf-8'))
ROOTS = [os.path.join(os.path.dirname(ROOT), 'schemas'), r'D:\AI\schemas']      # D:\DI first, then D:\AI

def repo_version(acr, idx):
    """The latest version of the standard (or of its part number idx) in the repository, as v<x>-<y>, or None."""
    folder = acr + (str(idx) if idx else '')
    for r in ROOTS:
        d = os.path.join(r, folder)
        if os.path.isdir(d):
            vs = sorted((v for v in os.listdir(d) if re.fullmatch(r'V\d+\.\d+', v)), key=vkey)
            if vs:
                return 'v' + vs[-1][1:].replace('.', '-')
    return None
out = {}
for s in table:
    acr = s['acronym']
    home = get(s['url'])
    if home is None:
        print('%-4s no home page' % acr)
        continue
    parts = [p.lower() for p in s.get('parts', [])] or ['']
    for idx, part in enumerate(parts, 1):
        pre = 'https://mpai.community/standards/mpai-%s/%s' % (acr.lower(), (part + '/') if part else '')
        versions = set(re.findall(re.escape(pre) + r'(v\d+-\d+)/', home))
        if not versions:
            # a part's home page may not link its versions from the standard page: try the part's page
            ph = get(pre) if part else None
            versions = set(re.findall(re.escape(pre) + r'(v\d+-\d+)/', ph or ''))
        rv0 = repo_version(acr, idx if s.get('parts') else 0)
        if not versions and not rv0:
            print('%-4s %-4s no version found' % (acr, part))
            continue
        versions = versions or {rv0}
        rv = repo_version(acr, idx if s.get('parts') else 0)
        ver = rv if rv and get(pre + rv + '/ai-modules/') is not None or rv and get(pre + rv + '/data-types/') is not None else max(versions, key=vkey)
        base = pre + ver + '/'
        entry = {'version': 'V' + ver[1:].replace('-', '.'), 'base': base, 'ai-modules': {}, 'data-types': {}}
        for page in ('ai-modules', 'data-types'):
            h = get(base + page + '/')
            if h is None:
                continue
            for m in re.finditer(r'<a[^>]+href="(%s%s/([a-z0-9-]+)/?)"[^>]*>(.*?)</a>' % (re.escape(base), page), h, flags=re.S):
                name = html.unescape(re.sub(r'<[^>]+>', '', m.group(3))).strip()
                if name:
                    entry[page][m.group(2)] = {'url': m.group(1), 'name': name}
        out.setdefault(acr, {})[part] = entry
        print('%-4s %-4s %-6s AIMs=%3d data types=%3d  %s' % (acr, part, entry['version'], len(entry['ai-modules']), len(entry['data-types']), base))
# the Process Actions of MMM-TEC are listed in a table of their page: MMM-<code> <name>
mmm = out.get('MMM', {}).get('tec')
if mmm:
    h = get(mmm['base'] + 'process-actions/') or ''
    text = html.unescape(re.sub(r'\s+', ' ', re.sub(r'<[^>]+>', ' ', h)))
    acts = {}
    for code, name in re.findall(r'(MMM-[A-Z0-9]{3}) ([A-Z][A-Za-z]+(?:-[A-Z][a-z]+)?)', text):
        if code != 'MMM-TEC':
            acts.setdefault(code, name)
    mmm['actions'] = acts
    print('MMM process actions on the web page:', len(acts))
json.dump(out, open(os.path.join(ROOT, 'weblinks.json'), 'w', encoding='utf-8'), indent=1, sort_keys=True)
print('written weblinks.json')
