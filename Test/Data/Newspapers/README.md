# Test pages for Newspaper Reading (MMC-NPR)

Pages of newspaper-like text, to show NPR working: a page and a Point at the top of a column go in; the text of that column comes
out as a Portable Avatar. The aim is to demonstrate NPR, not to measure the OCR (which is not ours); a modern, born-digital or
well-printed page is enough, so no historical scans are used.

Each folder holds the page (`page.png` or `page.jpg`) and `columns.json`:

| Field | Meaning |
|---|---|
| `Columns[].Box` | the box of the text of the column, in pixels of the page |
| `Columns[].Point` | a point at the top of the column, in pixels of the page: the Point a person would indicate |
| `Columns[].Text` | the true text of the column |

## synthetic/

Pages made (with `Test/Mpai.Aif.Tests/tools`) from articles of Wikinews (en, fr, de, it), set in three
columns under a masthead. The text of a column is exactly what was set in it. `*-clean` is the page at 300 dpi; `*-degraded` is
the same page as a scanner or a camera would give it (150 dpi, rotated 1.1 degrees, blurred, noisy, grey, JPEG quality 40), with
the boxes and points moved with it.

The articles are Wikinews texts, **CC BY 2.5** (<https://creativecommons.org/licenses/by/2.5/>); the title, address and revision of
each one is in `articles.json` of its folder. The layout is ours.

## real/

Pages of government publications, born digital, rendered from their PDF at 300 dpi; the text of each column is that of the text
layer of the PDF. `SOURCE.md` of each folder gives the address of the PDF, its SHA-256 and its licence.

- `us-federal-register-a`, `us-federal-register-b`: Federal Register, 26 October 2026; three columns; public domain.
- `uk-london-gazette`: The London Gazette, issue 63900; two columns; Open Government Licence v3.0.

The EU Official Journal (EUR-Lex) was meant to be here too, but EUR-Lex does not serve its PDFs to programs (it answers with a
challenge page).
