# ADR-0027: Localization and the bilingual data model

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

English and Arabic UI with full RTL; bilingual documents; date, number and time-zone formats; non-calendar fiscal years; future languages (Kurdish Sorani, Turkish, French) without schema changes.

## Decision

* **UI strings**: i18next resources per language, ICU messages; English is the source, Arabic is required for merge; other languages optional. Server-side messages (validation errors, emails, notifications, problem details) use the same resource files via a .NET ICU formatter, selected by `Accept-Language` or the user's locale.
* **Bilingual master data**: fields that print on documents or appear in reports are stored as a JSONB translation map `{"en": "...", "ar": "..."}` in a `name_i18n` column with a generated `name` column (the tenant's default language) for indexing and sorting; APIs return both the map and the resolved value for the request language with fallback (requested → tenant default → any). Entities: accounts, dimensions and values, items, categories, UoM, partners (legal name), addresses (free text lines), payment terms, delivery terms, tax codes, reason codes, print labels, custom field labels and options, report field labels.
* **Documents** store the language they were issued in (`document_language`) and, for bilingual templates, print both.
* **Numbers and dates**: `Intl` on the client and .NET globalization on the server, per user locale; digit shaping (Western or Eastern Arabic) is a per-user setting; the API always exchanges canonical ISO formats and decimal strings.
* **Collation**: PostgreSQL ICU collations for sorting Arabic and English names correctly; search normalisation rules in ADR-0023.
* **Time zones**: company time zone for posting dates; user time zone for display; explicit in the UI when they differ.
* **Calendars**: Gregorian storage; Hijri display optional (ADR-0011); working week and holidays per company.
* **Amount in words** in English and Arabic (with correct gender and dual forms for Arabic currency units) for cheques and invoices, implemented as a tested library in `Quicker.Kernel`.
* **Addresses**: structured (country-specific formats) with a formatted bilingual rendering; Iraq addresses support governorate, district and landmark lines.
* **RTL** in the UI via `dir="rtl"` on the document root, logical CSS properties, mirrored icons where meaning depends on direction (back/forward), and per-field direction detection for mixed content (`dir="auto"` on inputs).

## Alternatives considered

* **Separate `name_en`/`name_ar` columns.** Simple, but adding a language becomes a schema change across dozens of tables.
* **Translation tables per entity.** Normalised but verbose to query; JSONB with a generated default column is the pragmatic PostgreSQL answer.

## Consequences

* Adding a language is resource files plus data, never a migration.
* Bilingual documents are native, which is a real differentiator in the target market.
