# ADR-0022: Print templates and PDF rendering

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

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

## Amendment (2026-11-03): branded documents, the company's logo, the receipt

The trader (ADR-0035 §20, A-205) wants invoices and receipts in his company's identity. Four things changed, none of them special to one company:

* **A `brand` variant** (`PrintVariants.Brand`) of the sales invoice and the customer statement, and of the new receipt below: the company's logo on a dark band with the title and number, the brand typefaces, blue accents, the total in a black block, a footer rule in the brand colour. It is a shipped template like the others, copied into the workspace on first use and editable there; the standard layouts are untouched. A draft invoice still prints pro forma only.
* **The company's logo is a company setting**, `company.logo` (`CompanySettingKeys.Logo`), an image data URI of a few hundred kilobytes set on the Companies screen (file picker, preview, remove). `PrintService` hands it to every template as `company.logo`; a value that is not an image data URI is ignored, so a template never fetches from the network while it prints.
* **The customer receipt is a document of its own**: `bank_receipt` (the banking module's `ReceiptPrintModel`, standard and branded templates): the amount received and in words, from whom, into which box or account and how (cash, transfer, cheque with its number and bank, card), the invoices it settled with what remains on each, the customer's balance after it from the receivables subledger, two signature lines. Posted receipts are final and kept as evidence; a draft prints with a draft watermark, a reversed one with a reversed watermark.
* **Two more embedded fonts**, Montserrat (Latin) and Cairo (Arabic), both variable and under the SIL Open Font License beside DejaVu Sans; the branded templates set them, the standard ones keep DejaVu. The layout regression keeps baselines for the branded invoice and receipt too.

## Amendment (2026-10-31): share links and the print dialog

* A rendered PDF can be opened without signing in through a **share link** (`prt_share_links`, A-176): the token carries the tenant id and 32 random bytes and is handed out once; only its SHA-256 is stored, so a copy of the database yields no working links. Links expire (7 days by default, 30 at most), can be revoked and count their openings. The public endpoint (`/api/v1/public/prints/{token}`) switches the unit of work to the token's tenant and reads the row under RLS; unknown, tampered and revoked links read alike.
* **WhatsApp** is a share link in a `wa.me` message composed in the browser from the partner's phone; the server sends nothing and needs no WhatsApp account. A Business API adapter is a later addition behind the same link.
* The **print dialog** previews the template's HTML in a sandboxed frame (opaque origin) and prints through the PDF in the browser's viewer, so a template's markup can neither reach the app nor differ from what prints.
* The render **fingerprint** hashes the HTML with the generation time left out, so a document reuses its PDF across minutes; the time printed on a reused PDF is the time it was made.
* The **template screen** edits HTML and CSS as text with versions, a sample-data preview, rollback and copies; the block editor described above stays with the designer in M7.

