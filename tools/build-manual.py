"""
Builds the PDF user manual from the same HTML the intranet shows on /instrukcja.

    python tools/build-manual.py

Input:  src/Gaska.Intranet.Web/Help/instrukcja.html (+ screenshots in wwwroot/img/instrukcja)
Output: src/Gaska.Intranet.Web/Help/Instrukcja-Gaska-Intranet.pdf (served on /instrukcja/pdf)
        docs/Instrukcja-Gaska-Intranet.pdf (copy for sharing)

Needs Microsoft Edge or Google Chrome (headless "print to PDF") – nothing is downloaded.
"""
import datetime
import html
import os
import re
import shutil
import subprocess
import time
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "src" / "Gaska.Intranet.Web"
SOURCE = WEB / "Help" / "instrukcja.html"
WWWROOT = WEB / "wwwroot"
OUTPUTS = [WEB / "Help" / "Instrukcja-Gaska-Intranet.pdf", ROOT / "docs" / "Instrukcja-Gaska-Intranet.pdf"]

BROWSERS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/microsoft-edge",
]

PRINT_CSS = """
@page { size: A4; margin: 16mm 15mm 18mm; }
* { box-sizing: border-box; }
html, body { margin: 0; }
body { font-family: "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; font-size: 10.5pt; color: #262626; line-height: 1.5; }
.cover { height: 250mm; display: flex; flex-direction: column; justify-content: center; page-break-after: always; }
.cover img { width: 70mm; margin-bottom: 14mm; }
.cover h1 { font-size: 30pt; margin: 0 0 4mm; }
.cover .lead { font-size: 13pt; color: #555; margin: 0 0 18mm; }
.cover .meta { color: #777; font-size: 10pt; border-top: 3px solid #F9C211; padding-top: 4mm; width: 120mm; }
.toc { page-break-after: always; }
.toc h2 { font-size: 18pt; margin: 0 0 6mm; }
.toc ol { font-size: 12pt; line-height: 2; padding-left: 6mm; }
.manual section { page-break-before: always; }
.manual section:first-child { page-break-before: auto; }
.manual h2 { font-size: 18pt; margin: 0 0 4mm; padding-bottom: 2mm; border-bottom: 3px solid #F9C211; }
.manual h3 { font-size: 13pt; margin: 6mm 0 2mm; page-break-after: avoid; }
.manual h4 { font-size: 11pt; margin: 4mm 0 2mm; page-break-after: avoid; }
.manual p, .manual ul, .manual ol { margin: 0 0 3mm; }
.manual li { margin-bottom: 1mm; }
.manual figure { margin: 3mm 0 5mm; page-break-inside: avoid; }
.manual img { display: block; max-width: 100%; border: 1px solid #ddd; border-radius: 6px; }
.manual figcaption { font-size: 9pt; color: #666; margin-top: 1.5mm; }
.manual-tip { padding: 3mm 4mm; border-left: 4px solid #F9C211; background: #FFF6D6; border-radius: 4px; page-break-inside: avoid; }
.manual figure img[src*="/d-"] { max-height: 115mm; }
/* PDF = desktop version: phone screenshots and phone-only figures are left out. */
.manual .shot-m, .manual .only-m { display: none; }
.manual { counter-reset: chapter; }
.manual > section { counter-increment: chapter; }
.manual > section > h2::before { content: counter(chapter) ". "; }
.manual-figs { display: block; }
.manual-figs > figure { margin: 3mm 0 5mm; }
.manual-figs img { max-height: 95mm; }
.manual img[src*="user-menu"] { width: 75mm; }
"""


def find_browser() -> str:
    for candidate in BROWSERS:
        if os.path.exists(candidate):
            return candidate
    sys.exit("Microsoft Edge / Google Chrome not found – install one or add its path to BROWSERS.")


def build_html(body: str) -> str:
    # Screenshots: page-relative "img/..." → absolute file URLs (the PDF is printed from a temporary folder).
    body = re.sub(r'src="(img/[^"]+)"', lambda m: f'src="{(WWWROOT / m.group(1)).as_uri()}"', body)
    sections = re.findall(r'<section id="[^"]+">\s*<h2>([^<]+)</h2>', body)
    toc = "".join(f"<li>{html.escape(re.sub(r'^\d+\.\s*', '', t))}</li>" for t in sections)
    logo = (WWWROOT / "img" / "logo.png").as_uri()
    today = datetime.date.today().strftime("%d.%m.%Y")
    return f"""<!DOCTYPE html>
<html lang="pl"><head><meta charset="utf-8"><title>Instrukcja – Gąska Intranet</title><style>{PRINT_CSS}</style></head>
<body>
<div class="cover">
  <img src="{logo}" alt="Gąska">
  <h1>Intranet – instrukcja obsługi</h1>
  <p class="lead">Logowanie, nawigacja, instalacja aplikacji oraz działy: Pracownicy, RCP, Helpdesk IT i Kontrahenci.</p>
  <div class="meta">Wersja z dnia {today}<br>Aktualna wersja zawsze w intranecie: Pomoc → Instrukcja</div>
</div>
<div class="toc"><h2>Spis treści</h2><ol>{toc}</ol></div>
{body}
</body></html>"""


def main() -> None:
    body = SOURCE.read_text(encoding="utf-8")
    body = re.sub(r"<!--.*?-->", "", body, flags=re.S)
    browser = find_browser()

    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp) / "instrukcja.html"
        page.write_text(build_html(body), encoding="utf-8")
        pdf = Path(tmp) / "instrukcja.pdf"
        subprocess.run([browser, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                        "--allow-file-access-from-files", f"--user-data-dir={Path(tmp) / 'profile'}",
                        f"--print-to-pdf={pdf}", page.as_uri()],
                       check=True, timeout=180, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # Newer Edge builds hand the job to another process and return before the file is written – wait for it.
        deadline = time.monotonic() + 120
        last = -1
        while time.monotonic() < deadline:
            size = pdf.stat().st_size if pdf.exists() else -1
            if size >= 10_000 and size == last:
                break
            last = size
            time.sleep(1)
        if not pdf.exists() or pdf.stat().st_size < 10_000:
            sys.exit("The browser did not produce the PDF.")

        for target in OUTPUTS:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(pdf, target)
            print(f"{target.relative_to(ROOT)}  ({target.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
