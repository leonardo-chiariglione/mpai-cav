"""Pages of government publications, born digital: the page as a 300 dpi picture, and the text of each of its three columns from the text layer of the PDF.
python real_pages.py <out folder>
"""
import hashlib, json, os, re, sys
import pymupdf

PAGES = [
 # id, pdf, page (1-based), title, uri, licence
 ('us-federal-register-a', r'C:\Users\Thor\AppData\Local\Temp\pdf\fr1.pdf', 2, 'Federal Register, Vol. 91, No. 205, 26 October 2026, Rules and Regulations (Closed Captioning of Video Programming), page 2 of the document',
  'https://www.govinfo.gov/content/pkg/FR-2026-10-26/pdf/2026-21808.pdf', 'Public domain (work of the United States Government, 17 U.S.C. 105)', 'https://www.govinfo.gov/about/policies'),
 ('us-federal-register-b', r'C:\Users\Thor\AppData\Local\Temp\pdf\fr2.pdf', 2, 'Federal Register, Vol. 91, No. 205, 26 October 2026, Rules and Regulations (Enhancing the Ability of Consumers To Control the Robocalls), page 2 of the document',
  'https://www.govinfo.gov/content/pkg/FR-2026-10-26/pdf/2026-21821.pdf', 'Public domain (work of the United States Government, 17 U.S.C. 105)', 'https://www.govinfo.gov/about/policies'),
 ('uk-london-gazette', r'C:\Users\Thor\AppData\Local\Temp\pdf\gaz.pdf', 4, 'The London Gazette, issue 63900, page 4 of the PDF',
  'https://www.thegazette.co.uk/London/issue/63900/data.pdf', 'Open Government Licence v3.0', 'https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/'),
]
SCALE = 300 / 72

def columns(page, n=3):
    W, H = page.rect.width, page.rect.height
    blocks = [b for b in page.get_text('blocks') if b[6] == 0 and b[4].strip()]
    body = [b for b in blocks if b[1] > 55 and b[3] < H - 50 and (b[2] - b[0]) < W * 0.5]
    cols = [[] for _ in range(n)]
    for b in body:
        c = min(n - 1, int(((b[0] + b[2]) / 2) / (W / n)))
        cols[c].append(b)
    out = []
    for c in cols:
        c.sort(key=lambda b: (round(b[1]), b[0]))
        text = '\n'.join(re.sub(r'\s+', ' ', b[4].replace('\n', ' ')).strip() for b in c)
        box = [min(b[0] for b in c), min(b[1] for b in c), max(b[2] for b in c), max(b[3] for b in c)]
        first = c[0]
        out.append({'Box': [round(v * SCALE) for v in box], 'Point': [round((first[0] + first[2]) / 2 * SCALE), round((first[1] + first[3]) / 2 * SCALE)], 'Text': text})
    return out

def main(out):
    for id_, pdf, pg, title, uri, licence, licence_uri in PAGES:
        sha = hashlib.sha256(open(pdf, 'rb').read()).hexdigest().upper()
        d = pymupdf.open(pdf)
        page = d[pg - 1]
        folder = os.path.join(out, id_); os.makedirs(folder, exist_ok=True)
        page.get_pixmap(dpi=300, colorspace=pymupdf.csGRAY).save(os.path.join(folder, 'page.png'))
        cols = columns(page, 2 if id_ == 'uk-london-gazette' else 3)
        meta = {'Id': id_, 'Language': 'en', 'Dpi': 300, 'Width': round(page.rect.width * SCALE), 'Height': round(page.rect.height * SCALE),
                'Kind': 'real: a page of a government publication, rendered from its PDF', 'Columns': cols}
        json.dump(meta, open(os.path.join(folder, 'columns.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
        open(os.path.join(folder, 'SOURCE.md'), 'w', encoding='utf-8').write(
            f"# {id_}\n\n{title}\n\n- PDF: <{uri}>\n- SHA-256 of the PDF when the page was made: `{sha}`\n- Page: {pg}\n- Licence: {licence} (<{licence_uri}>)\n"
            f"- `page.png` is that page rendered at 300 dpi, grey; the text of each column in `columns.json` is taken from the text layer of the PDF.\n")
        print(id_, [len(c['Text']) for c in cols])

if __name__ == '__main__':
    main(sys.argv[1])
