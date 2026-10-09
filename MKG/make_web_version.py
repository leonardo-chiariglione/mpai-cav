"""Makes the web pages of a new version of a standard from those of the previous one.

The pages are read from mpai.community (the <h1> and the <article> of each page, which is what a page of the site holds),
the references to the version of the standard itself are changed (its web addresses, the links to its JSON schemas,
"MPAI-XXX Vx.y"); references to other standards and to the images already uploaded are left as they are.
AI Workflows become AI Modules: the pages of the workflows are written under ai-modules/.

    python make_web_version.py HMC "" 2.1 2.2          ->  web/mpai-hmc/v2-2/...
    python make_web_version.py NNW nnt 1.1 2.0         ->  web/mpai-nnw/nnt/v2-0/...

Each page is written as web/<path>/index.html (an HTML fragment: a heading and the content of the page) with the
list of what changed in web/<standard>/<version>/CHANGES.txt.
"""
import html as H, os, re, sys, urllib.request

ROOT = os.path.dirname(os.path.abspath(__file__))
RENAME_AIW = '--aiw' in sys.argv      # the AI Workflows are AI Modules (HMC V2.2, NNT V2.0), not for every standard


def get(url):
    try:
        return urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'}), timeout=30).read().decode('utf-8', 'ignore')
    except Exception:
        return None


def crawl(base):
    # some pages are linked with the version written with a dot (v1.1) where the address has a dash (v1-1)
    m = re.search(r'/(v\d+)-(\d+)/$', base)
    dotted = base[:m.start()] + '/%s.%s/' % (m.group(1), m.group(2)) if m else base
    seen, todo, pages = {base}, [base], {}
    while todo:
        u = todo.pop(0)
        h = get(u) or (get(u.replace(base, dotted)) if u.startswith(base) else None)
        if h is None:
            continue
        pages[u] = h
        for l in set(re.findall(r'href="((?:%s|%s)[^"#]*)"' % (re.escape(base), re.escape(dotted)), h)):
            l = l.replace(dotted, base)
            if l not in seen and '.' not in l.rsplit('/', 1)[-1]:
                seen.add(l); todo.append(l)
    return pages


def main(std, part, old, new):
    s = std.lower()
    pre = 'https://mpai.community/standards/mpai-%s/%s' % (s, (part + '/') if part else '')
    ov, nv = 'v' + old.replace('.', '-'), 'v' + new.replace('.', '-')
    base_old, base_new = pre + ov + '/', pre + nv + '/'
    pages = crawl(base_old)
    out_root = os.path.join(ROOT, 'web', 'mpai-' + s, *( [part] if part else []), nv)
    changes = []
    for url, h in sorted(pages.items()):
        rel = url[len(base_old):].strip('/')
        title = (re.search(r'<h1[^>]*>(.*?)</h1>', h, flags=re.S) or [None, ''])[1] if '<h1' in h else ''
        j, k = h.find('<article'), h.find('</article>')
        body = h[j:k + len('</article>')] if j >= 0 else ''
        n = {}
        def sub(pattern, repl, flags=0):
            nonlocal body
            body, c = re.subn(pattern, repl, body, flags=flags)
            if c:
                n[pattern] = n.get(pattern, 0) + c
        # the references to the version of the standard itself
        sub(re.escape(pre + ov + '/'), base_new)
        sub(re.escape(pre + 'v' + old + '/'), base_new)          # the version written with a dot
        sub(r'(schemas\.mpai\.community/%s/(?:%s/)?)V%s/' % (std.upper() + ('\\d' if part else ''), '' , re.escape(old)), r'\g<1>V%s/' % new)
        sub(r'(MPAI-%s\)? ?(?:Version )?)V?%s\b' % (std.upper(), re.escape(old)), lambda m: m.group(1) + ('V' if 'V' in m.group(0) else '') + new)
        sub(r'\b%s-([A-Z0-9]{3})-V%s\b' % (std.upper(), re.escape(old)), r'%s-\1-V%s' % (std.upper(), new))
        # the AI Workflows are AI Modules
        moved = rel
        if RENAME_AIW and rel.startswith('ai-workflows'):
            if rel == 'ai-workflows':
                changes.append('%s: not written - the AI Workflows are AI Modules; their page is ai-modules/' % rel)
                continue
            moved = 'ai-modules/' + rel.split('/', 1)[1]
        if RENAME_AIW:
            sub(re.escape(base_new) + r'ai-workflows/([a-z0-9-]+)/', base_new + r'ai-modules/\1/')
            sub(re.escape(base_new) + r'ai-workflows/', base_new + 'ai-modules/')
        if RENAME_AIW:
            sub(r'AI Workflows', 'AI Modules'); sub(r'AI Workflow', 'AI Module'); sub(r'\bAIWs\b', 'AIMs'); sub(r'\bAIW\b', 'AIM')
        d = os.path.join(out_root, *moved.split('/')) if moved else out_root
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, 'index.html'), 'w', encoding='utf-8') as f:
            f.write('<!-- %s : from %s -->\n<h1>%s</h1>\n%s\n' % (moved or '(home)', url, title.strip(), body))
        changes.append('%-60s %s' % ((moved or '(home)') + '/', ', '.join('%d x %s' % (c, p[:40]) for p, c in n.items()) or 'no change'))
    with open(os.path.join(out_root, 'CHANGES.txt'), 'w', encoding='utf-8') as f:
        f.write('Pages of %s%s %s, made from those of %s by make_web_version.py\n\n' % (std, ('-' + part.upper()) if part else '', new, old))
        f.write('\n'.join(changes) + '\n')
    print(out_root); print('\n'.join(changes))


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    main(args[0], args[1], args[2], args[3])
