"""Collects public-domain / CC0 music from Wikimedia Commons for the Tape Irregularity Classification
experiment (CAE-USC V2.5, CAE-TIC). Run with:  python -I collect_corpus.py

Only files whose Commons licence field says Public domain / CC0 / PD-* are kept. Each file is recorded
in corpus\\manifest.json with its Commons page, licence, author, duration and SHA-256.
Speech is not collected: the target is music (piano, guitar, voice singing, orchestra, and a few others).
"""
import hashlib, json, os, re, sys, time, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'corpus')
API = 'https://commons.wikimedia.org/w/api.php'
UA = 'MPAI-CAE-TIC-corpus/1.0 (research; leonardo@chiariglione.org)'

# group: (search queries, target minutes)
GROUPS = {
    'piano':     (['piano sonata', 'piano nocturne', 'piano etude', 'piano prelude', 'piano solo'], 60),
    'guitar':    (['classical guitar', 'guitar solo', 'acoustic guitar', 'guitar prelude'], 45),
    'voice':     (['soprano aria', 'tenor aria', 'sung song', 'baritone song', 'lied voice piano'], 45),
    'orchestra': (['symphony orchestra', 'orchestra overture', 'piano concerto orchestra', 'orchestral suite'], 60),
    'violin':    (['violin solo', 'violin sonata', 'string quartet'], 25),
    'organ':     (['organ fugue', 'pipe organ', 'organ toccata'], 20),
    'choir':     (['choir', 'chorus a cappella'], 20),
}
MAX_FILE_MB = 30
MAX_MIN_PER_FILE = 12
MIN_MIN_PER_FILE = 0.5
BAD = re.compile(r'speech|interview|lecture|podcast|reading|spoken|announcement|ringtone|sound effect|bird|noise|test tone|sample|synth demo', re.I)
LIC_OK = re.compile(r'^(public domain|cc0|cc-zero|pd[- ]|no restrictions)', re.I)


def get(params):
    url = API + '?' + urllib.parse.urlencode(params)
    for k in range(4):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': UA})
            return json.load(urllib.request.urlopen(req, timeout=60))
        except Exception as e:
            time.sleep(2 + 3 * k)
    return None


def search(query, limit=300):
    got, off = [], 0
    while off < limit:
        r = get({'action': 'query', 'list': 'search', 'srnamespace': 6, 'srsearch': query + ' filetype:audio',
                 'srlimit': 50, 'sroffset': off, 'format': 'json'})
        if not r or not r.get('query', {}).get('search'):
            break
        got += [s['title'] for s in r['query']['search']]
        if 'continue' not in r:
            break
        off = r['continue']['sroffset']
        time.sleep(0.3)
    return got


def info(titles):
    out = {}
    for i in range(0, len(titles), 40):
        r = get({'action': 'query', 'titles': '|'.join(titles[i:i + 40]), 'prop': 'imageinfo',
                 'iiprop': 'url|size|mime|extmetadata', 'format': 'json'})
        if not r:
            continue
        for p in r.get('query', {}).get('pages', {}).values():
            ii = (p.get('imageinfo') or [None])[0]
            if ii:
                out[p['title']] = ii
        time.sleep(0.3)
    return out


def meta(ii, k):
    return re.sub(r'<[^>]+>', '', (ii.get('extmetadata', {}).get(k, {}) or {}).get('value', '') or '').strip()


def main():
    os.makedirs(OUT, exist_ok=True)
    mpath = os.path.join(OUT, 'manifest.json')
    manifest = json.load(open(mpath, encoding='utf-8')) if os.path.exists(mpath) else {'files': []}
    have = {f['title'] for f in manifest['files']}
    for group, (queries, target_min) in GROUPS.items():
        os.makedirs(os.path.join(OUT, group), exist_ok=True)
        have_min = sum(f['minutes'] for f in manifest['files'] if f['group'] == group)
        titles = []
        for q in queries:
            for t in search(q):
                if t not in titles and t not in have:
                    titles.append(t)
        print(group, 'candidates', len(titles), flush=True)
        details = info(titles)
        for t in titles:
            if have_min >= target_min:
                break
            ii = details.get(t)
            if not ii or BAD.search(t):
                continue
            mime = ii.get('mime', '')
            if not mime.startswith(('audio/', 'application/ogg')):
                continue
            lic = meta(ii, 'LicenseShortName') or meta(ii, 'License')
            if not LIC_OK.match(lic):
                continue
            size = ii.get('size', 0)
            if size > MAX_FILE_MB * 1024 * 1024 or size < 150 * 1024:
                continue
            dur = ii.get('duration')
            if dur is None:
                continue
            minutes = dur / 60.0
            if minutes < MIN_MIN_PER_FILE or minutes > MAX_MIN_PER_FILE:
                continue
            name = re.sub(r'[^A-Za-z0-9._-]+', '_', t.replace('File:', ''))[:90]
            path = os.path.join(OUT, group, name)
            try:
                req = urllib.request.Request(ii['url'], headers={'User-Agent': UA})
                data = urllib.request.urlopen(req, timeout=120).read()
            except Exception as e:
                print('  fail', t, e, flush=True)
                continue
            open(path, 'wb').write(data)
            manifest['files'].append({
                'group': group, 'title': t, 'file': os.path.relpath(path, OUT).replace('\\', '/'),
                'url': ii['url'], 'page': ii.get('descriptionurl', ''), 'licence': lic, 'licenceUrl': meta(ii, 'LicenseUrl'),
                'author': meta(ii, 'Artist'), 'credit': meta(ii, 'Credit')[:200], 'minutes': round(minutes, 2),
                'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
            have.add(t)
            have_min += minutes
            json.dump(manifest, open(mpath, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
            print('  +', group, round(have_min, 1), 'min', lic, name, flush=True)
            time.sleep(0.5)
    tot = {}
    for f in manifest['files']:
        tot[f['group']] = tot.get(f['group'], 0) + f['minutes']
    print('TOTAL minutes by group', {k: round(v, 1) for k, v in tot.items()}, 'files', len(manifest['files']),
          'MB', round(sum(f['bytes'] for f in manifest['files']) / 1e6, 1), flush=True)


main()
