# M2 Core accounting — report to the founder

Date: 2026-09-22. Branch `claude/efficient-request-ajd9zy`, 8 commits since the M1 report (one per slice; 2.5 in two). No pull request has been opened; say the word and one will be.

## 1. What was built

Every slice of the M2 roadmap (`docs/ROADMAP.md`, 2.1–2.7) is done. In one sentence: a general ledger that only the posting engine can write, with charts, profiles, journals, period control, inquiry in both currencies, an invariant harness that runs after every scenario, and a demo tenant carrying a year of books that the harness verifies before it commits.

| Slice | Delivered |
|-------|-----------|
| 2.1 Chart of accounts | Charts shared or per company; account tree with types, categories (statement mapping), header/postable, control flags with subledger type, currency restriction, manual-posting flag, cash-flow category, a default posting role; dimension rules per account (required, optional with default, blocked); templates `IFRS_SME`, `GCC`, `IRAQ_UAS` (bilingual, every posting role covered); statutory chart reference data and mappings; CSV/JSON import and export; `POST /charts/from-template` gives a new company a chart and a posting profile in one call. |
| 2.2 Posting engine | `IPostingService`: posting profiles versioned per company, rules with most-specific match, three-currency lines (transaction, functional, reporting) with per-line rounding through the company's policy and a rounding-difference line, period and permission checks, control-account/subledger cross-check, append-only entries and lines (partitioned by year) with a database trigger that refuses an unbalanced or incomplete entry at commit, derived balances incremented under a row lock, gapless `JE-{yyyy}-{seq:6}` numbers, audit and outbox events, idempotent replay, reversal into the first open period, verify and rebuild of balances. |
| 2.3 Journals | Manual journals draft → approval → post (company setting for approval, submitter cannot approve, `MJ-{yyyy}-{seq:5}` at posting), opening journals balanced on equity, accruals with automatic reversal, recurring templates (cron in the company's time zone; fixed, percentage or variable amounts), deferral schedules (prepayments, accruals, deferred revenue; to the minor unit with the remainder on the last line), CSV/JSON import, and the daily routines (auto-reversals, recurring generation, deferral postings) as a platform job and an endpoint. |
| 2.4 Periods and locks | Allow-posting windows per company or per role, corrections (reversal plus replacement in one transaction, into the first open period, linked both ways) for entries and for manual journals, reopen with permission, reason, recent authentication and audit; scenario 7 proven end to end. A platform-wide bug found on the way (refused requests were committed when the endpoint returned a typed result union) is fixed and asserted. |
| 2.5 Inquiry and reports | Trial balance at any date with a movement window, comparative, functional or reporting basis, dimension filters and grouping, every row drilling to its ledger; account ledger with opening, running balance, keyset paging and source-document links; balances by dimension; journal browser filters; CSV and XLSX exports generated in process and audited. Six web screens (chart, journals, trial balance, ledger, journal entries, period control) in English and Arabic with a browser journey under axe. |
| 2.6 Invariant harness v1 | `Quicker.Integrity`: six read-only checks (entries balanced in all three currencies, trial balance zero, derived balances equal the lines row by row, audit chain intact, tenant isolation from the catalogue, gapless numbering) as an endpoint, a daily platform job that fails naming the tenant and checks, and the last step of every accounting scenario test. Tampering tests prove it catches an altered balance, an altered posted line and a broken audit event. |
| 2.7 Demo seed v2 | `make demo` now gives each demo company a chart from the template that fits it, a posting profile, cost centres, departments and projects, customers and suppliers on the control accounts, and twelve months of posted books (opening, prepayment and deferred-revenue schedules, recurring rent, monthly sales, purchases, utilities, collections, payments, salaries by cost centre, accruals with their reversals, bank charges, quarterly rent, a project journal), the routines run, earlier months closed, one correction, and the harness green before the seed commits. |

Migrations `V0010`–`V0013` and seeds `S0003`–`S0005`; every new tenant table is covered by the schema and isolation suites.

## 2. How to see it working

```
make up          # PostgreSQL, MinIO, Mailpit, migrations, seeds, demo tenant, API, worker, web
open http://localhost:5173   # sign in: owner@quicker.example / DemoPass2026!  (accountant@… opens the Arabic UI)
make demo        # rebuild the demo tenant with its year of books (about half a minute)
```

Things worth clicking through, in this order:

1. **Trial balance** (`g t`): pick Al-Rafidain Trading, it balances in IQD; switch the basis to USD, set a comparative date three months back, group by cost centre. Click any account: the ledger opens on exactly that figure with a running balance.
2. **Account ledger** (`g l`): every line links to its journal entry and to the manual journal that produced it. Download the CSV or XLSX (the Arabic UI produces a right-to-left sheet).
3. **Journal entries** (`g e`): filter by number prefix `MJ-`, manual only, or an account; open the eighth month's corrected purchase of `IQT` and follow the links to its reversal and replacement.
4. **Journals** (`g u`): draft a journal (rent against accrued expenses), post it; try a control account without a subledger item and read the refusal; try a date in a hard-closed month and read which permission would be needed.
5. **Period control** (`g p`): months before the previous one are hard-closed; reopen one with a reason (recent sign-in required), then look at the audit explorer to see who reopened it and why.
6. **Chart of accounts** (`g h`): the tree with control flags and dimension rules; the account editor.
7. **Platform → integrity**: `POST /api/v1/platform/integrity/run` answers with the six checks and what each examined.

Without Docker: `make migrate`, `make demo`, `make api`, `make web` against a local PostgreSQL 16+ (`docs/PROGRESS.md`, "How to run what exists", lists every endpoint family with example calls).

## 3. Test results

* `.NET`: 204 tests (203 pass, 1 S3 test skipped without MinIO; CI runs it against MinIO), every one on a real PostgreSQL database. M2 added the chart, posting engine, journals, period control, inquiry, integrity and demo suites; every accounting scenario ends with the invariant harness, and the demo seed runs it before it commits. Hard scenarios proven: the determination matrix, an unbalanced entry refused by the database itself (raw SQL as the schema owner), rebuild equals incremental, a prepayment amortised over 12 periods to the cent, auto-reversal on its date and not before, scenario 7 (correction into the open period, reopen refused without permission, audit shows who and why, posting windows per role), the trial balance zero at three sampled dates with every figure drilling to its lines, the harness failing on a deliberately corrupted balance row.
* Web: 2 unit tests, the design system's 46 axe checks, 3 Playwright journeys (admin in English and Arabic, accounting) with `axe-core` on every screen.
* CI (`.github/workflows/ci.yml`): unchanged in shape; the branch is green locally on every job and CI runs on push.
* Demo seed: 332 journals, 410 ledger entries, 33 period closings, about 32 s locally, identical counts and fixed ids across a reseed.

## 4. Decisions made along the way (all recorded in `docs/ASSUMPTIONS.md` A-088…A-095 and the ADRs)

* **A journal entry has no status column.** "Reversed", "corrected" and "auto-reversal" are link rows, and a unique index makes a second reversal impossible; posted lines are append-only for every role, the schema owner included.
* **Three amounts on every line** (transaction, functional, reporting) with both rates, each line rounded through the company's policy and the entry closed with a rounding-difference line, so the books balance in all three currencies and the harness checks all three.
* **Manual lines may touch a control account only when they name the subledger item** they adjust; without one the control account refuses manual posting, so subledgers stay reconciled.
* **A company's first posting profile is effective from the beginning of time** and retired versions still govern their dates, so opening balances and back-dated go-live entries post and a new version never orphans earlier dates.
* **Corrections are reversal plus replacement in one transaction** into the first open period, linked both ways; a journal is corrected once and a reversed entry is not corrected.
* **Posting windows are per role or for everyone**; the widest of a person's role windows applies, owners are held to the company window, jobs and seeders are exempt.
* **Inquiry reads the lines, never the derived balances**; the derived table is a cache that the harness compares with the lines both ways. Exports are generated in process (no spreadsheet library).
* **The harness tolerates nothing**: exact decimal equality, a run after every scenario test, a daily job that fails loudly like a broken audit chain.
* **The integrity module is named `Quicker.Integrity`**, because the build reserves the `*.Invariants` suffix for test projects.
* **The demo books are posted through the services as the system actor**, never written to the tables, so the demo is proof that the engine, the routines, the period control and the correction path work together on real volume.

## 5. Known gaps (the full list with reasons is in `docs/PROGRESS.md`)

* Reversals mirror the journal only; the subledger rows (open items, value entries, asset and bank transactions) are reversed by their modules when they arrive in M3–M6.
* The Iraq statutory chart is mapped at group level (Q9); detailed codes wait for an accountant's validation. Statutory reports belong to M6.
* Accounting screens not yet in the web pass: statutory mappings, chart import/export, posting profiles and rules, recurring templates, deferral schedules, the routine log and journal attachments (all available through the API). The journal editor takes account codes and amounts; dimensions and subledger items on lines are API-only until the pickers arrive with the partner and item masters. The trial balance screen takes one dimension filter at a time.
* Account changes are not yet guarded by postings (type, chart or header changes on an account that already has lines).
* Posting windows are per role, not per user or per module; the close checklist and year-end close are M6 work.
* The daily routine runs at 00:30 UTC for every tenant; a per-tenant time can be set in `ops.schedules`.
* The demo's customers and suppliers are subledger references, not partner masters, and its sales and purchases are manual journals, not documents; that is what M3–M5 add.
* Role templates do not yet grant `numbering.*` or `platform.*`; the owner and admin wildcards cover them.

## 6. What you can do now

1. **Review and merge**: ask for a pull request from `claude/efficient-request-ajd9zy` to `main` (none exists yet).
2. **Q9** in `docs/ASSUMPTIONS.md`: the edition of the Iraq Unified Accounting System code list and whether an accountant can validate the mapping. Everything works on the group mapping meanwhile.
3. **Try the books**: the demo's three companies differ in currency and template; the eighth month of `IQT` holds the correction, the previous month is soft-closed, everything before is hard-closed.

## 7. What is next

M3 Inventory in roadmap order: items and units of measure, warehouses and the stock ledger, the costing engine (the first subledger that reconciles to its control account through the harness), adjustments, transfers and assemblies, lots and serials, counts, replenishment, mobile scanning v1, then demo seed v3 with items and stock. I pause here for your review unless told to continue.
