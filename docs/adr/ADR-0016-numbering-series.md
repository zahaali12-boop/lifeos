# ADR-0016: Numbering series: gapless and non-gapless

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Numbering per company, branch, fiscal year and document type; gapless where the law requires it (tax invoices in most VAT regimes, ZATCA requires sequential invoice counters); no duplicates under concurrency; drafts must be identifiable before they get a legal number.

## Decision

* `num_series` (per tenant): `code`, `document_type`, `company_id`, `branch_id` (nullable), `fiscal_year_id` (nullable), `prefix`/`suffix` template (`INV-{branch}-{yy}-{seq:6}`), `next_number`, `gapless bool`, `reset_policy` (never | yearly | monthly), `valid_from/valid_to`, `is_default`. Selection rule: most specific series matching (document type, company, branch, year) wins; a document can override to any series the user's role permits.
* **Gapless series** allocate inside the posting transaction: `SELECT ... FOR UPDATE` on the series row, take `next_number`, increment, write the number on the document, commit. A rolled-back posting rolls back the allocation, so no gap and no duplicate. Drafts and unposted documents display a **draft identifier** (`DRAFT-<short id>`) and are never printed as invoices (the print template refuses).
* **Non-gapless series** (quotes, orders, internal transfers) allocate at creation using the same row-lock mechanism (cheap) so numbers are monotonic; cancelled drafts leave gaps, which is acceptable for these documents.
* **Reversals and credit notes** get their own series (never reuse the original number); the link is a field.
* **Void policy**: a posted gapless document cannot be deleted; it is reversed or credited, so the number sequence remains complete for auditors.
* **Year reset** uses the company's fiscal year; the series template can embed the year to keep numbers unique across years.
* **Import of legacy numbers**: opening documents can carry external numbers in `external_number`; series start values are configurable so live numbering continues from the legacy system.
* **Audit**: series changes (start value, prefix) are audited; decreasing a counter requires the `numbering.counter.reset` permission and a reason, and never goes below a number already issued.
* **Implementation note (M1.7)**: the counter lives in `num_series_counters`, one row per series and reset period, so yearly and monthly resets are separate counters rather than an in-place rewind; every issued number is recorded in the append-only `num_allocations` (unique per series/period/number and per series/document), which is what the gapless audit report reads.
* Concurrency proof: parallel posting test (ADR-0009) shows consecutive numbers; a gapless audit report lists every series with any missing numbers (expected: none).

## Alternatives considered

* **PostgreSQL sequences.** Fast and lock-free, but not transactional (gaps on rollback) and not per-scope without a sequence per series. Used nowhere for legal numbers.
* **Allocating numbers at draft creation.** Creates gaps when drafts are discarded, which fails tax audits.

## Consequences

* Gapless series serialise posting per series for a few milliseconds; acceptable, and per-branch series spread the load.
* Drafts are visibly drafts, which also prevents "draft invoices" being sent to customers.
