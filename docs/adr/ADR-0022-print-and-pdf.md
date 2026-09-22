# ADR-0022: Print templates and PDF rendering

Status: proposed · Date: 2026-09-22

## Context

Designable print templates for invoices, orders, statements and more, including bilingual (English and Arabic side by side or mirrored) layouts, QR codes for e-invoicing, company branding per company and branch, and reliable Arabic shaping and right-to-left text.

## Decision

* Templates are **HTML + CSS** using the **Scriban** template language (Liquid-compatible syntax, sandboxed, no arbitrary code), stored per tenant in `prt_templates` (document type, name, language mode: `en | ar | bilingual`, paper size, version, is_default, company/branch scope, html, css, header/footer fragments, sample data for preview).
* A **template designer** in the admin UI: start from shipped templates, edit blocks (logo, addresses, line table columns, totals, tax summary, bank details, QR, terms), switch language mode, live preview with real documents, version history and rollback.
* Rendering by **headless Chromium** (Playwright) in the worker: Chromium's text engine handles Arabic shaping, bidi, ligatures and fonts correctly; fonts bundled (Noto Sans Arabic, IBM Plex Sans Arabic, Inter) and embedded in PDFs; `@page` CSS for margins, page numbers, repeating table headers; PDF/A-3 optional for archival with the e-invoice XML attached (ZATCA and Peppol expect XML plus PDF).
* Data contract: each document type exposes a **print model** (JSON) that includes bilingual master data, formatted numbers per locale, words-of-amount in both languages, tax breakdown, payment schedule, custom fields flagged `show_on_print`, barcodes and QR payloads. The model is versioned with the API.
* Outputs: PDF for print/email/download; the same template renders in the browser for print preview; labels (barcode labels for bins, items, lots) use the same pipeline with label paper sizes.
* Caching: rendered PDFs of posted documents are stored in object storage keyed by document version and template version; a re-print reuses the file unless the template changed (posted documents can be re-rendered with a newer template but the original PDF is preserved as evidence).
* Email delivery uses templates (subject/body) with the PDF attached; sends are logged on the document timeline.

## Alternatives considered

* **Report designers (Crystal, DevExpress, Stimulsoft).** Proprietary, weaker Arabic/bidi fidelity, and not web-native.
* **Server-side PDF libraries (QuestPDF, iText).** Fast and no browser dependency, but Arabic shaping and complex bidi tables are exactly where they need extra work; HTML/CSS gives designers a familiar model.
* **LaTeX/Typst.** Excellent typography, unfamiliar to admins.

## Consequences

* Chromium in the worker image adds ~300 MB; acceptable, and Playwright is already used for tests.
* Bilingual documents are a first-class template mode, not a hack.
