"""Makes the pages of the AIF for each environment: an index and one page for Windows, Linux, macOS, Android and iOS.

What a page says of a build - version, operating system, address, SHA-256, size - is read from the description the build wrote
(MPAIStore/AIF/aif-*.json, made by make-aif-package.ps1); what it says of a platform without a build comes from platforms.json.
Adding an environment is adding its description and its entry in platforms.json, then running this program again:

    python MPAIStore/AIF/site/make-aif-pages.py
"""
import html, json, os

HERE = os.path.dirname(os.path.abspath(__file__))
AIF = os.path.dirname(HERE)
cfg = json.load(open(os.path.join(HERE, 'platforms.json'), encoding='utf-8-sig'))
NAME = cfg['AccessProgramName']
E = html.escape

CSS = """
:root { --bg:#ffffff; --fg:#10243e; --muted:#566b7f; --card:#eaf0f5; --accent:#0e8c98; --accent-fg:#ffffff; --line:#c9d6e2; --warn:#e8962e; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#0d1b2a; --fg:#e8eef5; --muted:#9fb3c8; --card:#16293e; --accent:#2bb3c0; --accent-fg:#0d1b2a; --line:#2a4258; --warn:#f0a94a; } }
:root[data-theme="dark"] { --bg:#0d1b2a; --fg:#e8eef5; --muted:#9fb3c8; --card:#16293e; --accent:#2bb3c0; --accent-fg:#0d1b2a; --line:#2a4258; --warn:#f0a94a; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--fg); font:16px/1.55 Calibri, "Segoe UI", system-ui, sans-serif; }
main { max-width: 52rem; margin: 0 auto; padding: 2rem 1rem 4rem; }
h1 { font: 700 2rem/1.2 Cambria, Georgia, serif; margin: .2em 0 .3em; text-wrap: balance; }
h2 { font: 700 1.3rem/1.3 Cambria, Georgia, serif; margin: 2rem 0 .5rem; }
p, li { max-width: 42rem; }
a { color: var(--accent); }
.crumbs { font-size: .9rem; color: var(--muted); }
.lead { font-size: 1.15rem; color: var(--muted); }
.grid { display:grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: 1rem; margin: 1.5rem 0; }
.card { background: var(--card); border:1px solid var(--line); border-radius: .6rem; padding: 1rem 1.1rem; }
.card h3 { margin:.1rem 0 .3rem; font: 700 1.15rem Cambria, Georgia, serif; }
.tag { display:inline-block; font-size:.78rem; letter-spacing:.04em; text-transform:uppercase; padding:.1rem .5rem; border-radius:1rem; border:1px solid var(--line); color: var(--muted); }
.tag.available { background: var(--accent); color: var(--accent-fg); border-color: var(--accent); }
table { border-collapse: collapse; width: 100%; margin: .5rem 0 1rem; }
th, td { text-align:left; padding:.45rem .7rem; border-bottom:1px solid var(--line); vertical-align: top; }
th { width: 11rem; color: var(--muted); font-weight: 600; }
.button { display:inline-block; background: var(--accent); color: var(--accent-fg); text-decoration:none; font-weight:700; padding:.7rem 1.2rem; border-radius:.5rem; }
code, pre { font: .92rem Consolas, "Cascadia Mono", monospace; }
pre { background: var(--card); border:1px solid var(--line); border-radius:.5rem; padding:.7rem .9rem; overflow-x:auto; }
.hash { word-break: break-all; }
.note { border-left: 4px solid var(--warn); padding:.2rem 0 .2rem .9rem; color: var(--muted); }
nav.platforms { display:flex; flex-wrap:wrap; gap:.5rem; margin:.5rem 0 0; }
nav.platforms a { text-decoration:none; border:1px solid var(--line); border-radius: 1rem; padding:.15rem .8rem; color: var(--fg); }
nav.platforms a[aria-current] { background: var(--card); font-weight:700; }
"""


def page(title, body, current=None):
    nav = ''.join(
        f'<a href="{p["Id"]}.html"{" aria-current=\"page\"" if p["Id"] == current else ""}>{E(p["Title"])}</a>'
        for p in cfg['Platforms'])
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{E(title)}</title><style>{CSS}</style></head>
<body><main>
<div class="crumbs"><a href="index.html">The AIF</a></div>
<nav class="platforms" aria-label="Environments">{nav}</nav>
{body}
</main></body></html>
"""


def size(n):
    return f'{n / 1048576:.1f} MB'


def steps_windows(d):
    zipname = d['Address'].rsplit('/', 1)[-1]
    return f"""
<h2>Install it</h2>
<ol>
<li>Install the <strong>{E(d.get('Requires', ''))}</strong> if your computer does not have it.</li>
<li>Download <a href="{E(d['Address'])}">{E(zipname)}</a> ({size(d['SizeBytes'])}) and check it (below).</li>
<li>Unzip it in any folder, with any name: spaces and accents are fine. The folder may be read-only: nothing is written there.</li>
<li>Copy <code>mas-server.store.example.json</code> to <code>mas-server.json</code> and write the address of the MPAI Store in <code>StoreUrl</code> and the Apps you want in <code>Apps</code>.</li>
<li>Start it: <code>service\\MasService.exe mas-server.json</code>.</li>
</ol>
<h2>Check what you downloaded</h2>
<p>The SHA-256 of the file is:</p>
<p class="hash"><code>{E(d['Sha256'])}</code></p>
<p>In PowerShell, in the folder of the file:</p>
<pre>Get-FileHash {E(zipname)} -Algorithm SHA256</pre>
<p>The two must be the same. If they are not, do not use the file.</p>
<h2>What it does the first time</h2>
<ul>
<li>It takes the L3s of the Apps you chose from the MPAI Store.</li>
<li>It fetches the package of each AIM from the address its L3 names, and asks the Store whether it is the one the Store approved. A package the Store does not approve is not loaded.</li>
<li>It obtains the models, voices and programs that each AIM declares, once, and checks their SHA-256. Models can be a gigabyte or more, so the first start needs an Internet connection and disk space.</li>
</ul>
<h2>Where it keeps what it obtains</h2>
<ul>
<li>Models, voices and programs: <code>%LOCALAPPDATA%\\MPAI\\AIF\\Access</code>. Another place: the environment variable <code>MPAI_ACCESS</code>.</li>
<li>The packages of the AIMs: the folder named by <code>PackageCache</code> in the configuration.</li>
</ul>
"""


def access_program(status):
    return f"""
<h2>The access program</h2>
<p>The AIF is the environment in which AI Modules run. What a person opens to reach the world of MPAI Apps is a program that
uses it: <strong>{E(NAME)}</strong> (a provisional name). It lists the Apps that the MPAI Store offers, asks before the first download
and opens an App in a window that follows its workflow.</p>
<p class="note">{E(NAME)} is not available yet. {status}</p>
"""


def build():
    out = {}
    cards = []
    for p in cfg['Platforms']:
        d = None
        if p.get('Description'):
            d = json.load(open(os.path.join(AIF, p['Description']), encoding='utf-8-sig'))
        status = p['Status']
        tag = f'<span class="tag{" available" if status == "available" else ""}">{E(status)}</span>'
        cards.append(f'<a class="card" style="text-decoration:none;color:inherit" href="{p["Id"]}.html"><h3>{E(p["Title"])}</h3>{tag}<p>{E(p["Summary"])}</p></a>')

        if d:
            facts = f"""<table>
<tr><th>Version</th><td>AIF V{E(d['Version'])}</td></tr>
<tr><th>Operating system</th><td>{E(d['OperatingSystem'])}</td></tr>
<tr><th>Processor</th><td>{E(d['Architecture'])}</td></tr>
<tr><th>Needs</th><td>{E(d.get('Requires', ''))}</td></tr>
<tr><th>Size</th><td>{size(d['SizeBytes'])}</td></tr>
</table>"""
            body = f"""<h1>The AIF for {E(p['Title'])}</h1>
<p class="lead">{E(d['Description'])}</p>
<p><a class="button" href="{E(d['Address'])}">Download for {E(p['Title'])}</a></p>
{facts}
{steps_windows(d)}
{access_program('Until it is, you start the AIF as above and use the Apps through a client of the MPAI-MAS Service it runs, such as Thalia.')}"""
        else:
            how = {
                'linux': 'The AIF for Linux is in preparation.',
                'macos': 'The AIF for macOS is planned.',
                'android': 'On Android the access program is planned as a client of an MPAI-MAS Service: the phone captures the voice, the camera and the touch and shows the avatar, and the AI Modules run on the Service. No AI is needed on the phone.',
                'ios': 'On iOS the access program is planned as a client of an MPAI-MAS Service: the iPhone or iPad captures the voice, the camera and the touch and shows the avatar, and the AI Modules run on the Service. No AI is needed on the device.',
            }[p['Id']]
            body = f"""<h1>The AIF for {E(p['Title'])}</h1>
<p class="lead">{E(p['Summary'])}</p>
<p>{tag}</p>
<p>{E(how)}</p>
<p>The Apps and their L3s are the same on every environment: what changes is the package of the AIF and the packages of the AIMs
that are made for it. Meanwhile the <a href="windows.html">AIF for Windows</a> is available.</p>
{access_program('It will come with the AIF for this environment.')}"""
        out[p['Id'] + '.html'] = page(f'The AIF for {p["Title"]}', body, p['Id'])

    index = f"""<h1>The AIF: the AI environment</h1>
<p class="lead">The AI Framework is a layer above the operating system. MPAI Apps depend on it and not on Windows, Linux or the phone you hold.
Choose your environment.</p>
<div class="grid">{''.join(cards)}</div>
<h2>What you get</h2>
<ul>
<li>An environment where AI Modules run under a Controller, with their data and their trust looked after.</li>
<li>Access to the MPAI Store: the Apps and AI Modules it has approved, checked again each time they are loaded.</li>
<li>One program, <strong>{E(NAME)}</strong> (provisional name), to reach the world of MPAI Apps.</li>
</ul>"""
    out['index.html'] = page('The AIF: the AI environment', index)
    for name, text in out.items():
        open(os.path.join(HERE, name), 'w', encoding='utf-8', newline='\n').write(text)
    print('made', ', '.join(sorted(out)))


build()
