| 6.6 Fixed assets | Categories, books (IFRS, tax), methods and conventions, acquisition from invoices/stock, depreciation runs, revaluation, impairment, transfers, disposals, register and schedules. | FA subledger equals FA control accounts; depreciation to the cent over the asset life. **Built 2026-10-31 (A-175)**: every method and convention to the cent, runs that catch up and reverse, additions, impairment, revaluation, transfers, full and partial disposals, opening balances, verification, register and reconciliation, invariant `fixed_assets_match_register`. Open: capitalising from stock, component roll-up, intercompany transfers. |# Quicker ERP: Roadmap

Milestones M1–M10 from the brief, refined into vertical slices. Each slice is a thin end-to-end path (database → posting → API → UI → tests) that leaves the system runnable and fully tested. Every milestone ends with the founder's review and a report (built, how to see it, test results, decisions, gaps, next).

The definition of done for every milestone is fixed by the brief: working code and migrations running with one command; unit, integration and end-to-end tests including the milestone's hard scenarios, green in CI; accounting invariants verified automatically; the seeded demo tenant extended; docs, ADRs and PROGRESS.md updated; a short report.

## Sequencing decisions (deviations from a literal reading of the milestone list)

| Decision | Why |
|----------|-----|
| The **workflow engine core** (definitions, rules, steps, requests, blocks, overrides; configured through the API) is built in **M4**, and its designer UI, delegation, escalation timers and mobile inbox are completed in M8. | Purchasing approvals, three-way-match blocks (M4) and credit holds (M5) need a real engine, not a stub. |
| The **print pipeline** and three shipped bilingual templates (invoice, order, statement) ship in **M5**; the template designer ships in M7. | Customers cannot sell without printable invoices. |
| **Financial statements v1** (trial balance, P&L, balance sheet, cash flow with comparatives) ship in **M6** on a first version of the layout engine; the visual designer and semantic layer ship in M7. | Period close needs statements; the designer needs the reporting schema. |
| The **tax engine** is M5 as planned; **e-invoicing adapters** (ZATCA, Peppol) are M8 with the provider interface defined in M5. | Adapters need certification work that should not block order-to-cash. |
| The **mobile scanning app** starts in M3 (counts, transfers) and grows in M4 (receiving) and M5 (picking). | Each flow ships with the module that owns it. |

## Founder programme (2026-10-31)

The founder reviewed the product after the M5 returns slice and set the order of work from here, with two aims: the everyday screens must be easy (Odoo is the bar) and the product must be hard to compete with in its market (Iraq and the Gulf: cash and cheques, Arabic first, local e-invoicing). The roadmap's slices stay as the definitions of done; this list fixes their order and adds what the roadmap lacked (expenses as the founder described them, point of sale, payroll, the rep app, the customer portal, light manufacturing). Each item is recorded in `docs/PROGRESS.md` as it lands.

| # | Work | Covers |
|---|------|--------|
| 1 | **UI ease**: a home dashboard with today's numbers and drill-down; document screens with a status bar and the next action only; quick create from anywhere and inline create of a customer, supplier or item; smart defaults and advanced fields hidden; keyboard-first line entry with paste from Excel; saved views with filters and group-by over 25–30 fields (the founder's "group by" request, pulled into this phase because the grid and the saved views are one piece of work); a first-run setup wizard with Excel import of customers, suppliers, items and opening balances. | part of 1.10, 7.5 (first cut), 8.3, 8.5 |
| 2 | **Expenses** (the founder's request): every expense recorded with its details and date, confirmation posts the accrual, payment on its own date posts the settlement, all through the posting engine; petty cash funds and replenishment. | 6.4 |
| 3 | **Fixed assets and depreciation**, more capable than the systems around us (multiple books, every method and convention, components, revaluation, impairment, transfers with history, partial disposals, verification, GL tie-out). | 6.6 |
| 4 | Print and send completion: template screen, print and email buttons on documents, WhatsApp. Done 2026-10-31 (A-176). | 5.9 (rest) |
| 5 | Cheques and post-dated cheques, bank statement import and reconciliation. | 6.2, 6.3 |
| 6 | E-invoicing: ZATCA phase 2. | 8.6 (first adapter) |
| 7 | Point of sale, offline-capable. Done 2026-10-31 (A-180, ADR-0032): the `Quicker.Pos` module (registers, sessions, orders posted on sync through the engines), the `/pos` till that sells from a cached catalogue offline, the back office at `/sales/pos`, receipts and ZATCA simplified invoices. | new |
| 8 | Payroll and HR, simple. Done 2026-10-31 (A-181, ADR-0033): the `Quicker.Payroll` module (pay components as posting keys, employees over partners with the employee role, leave, monthly runs prorated over thirty days, approved through the workflow engine, posted with each employee's net as a payable and paid through Banking), the payslip, and the screens under "People and payroll". | new |
| 9 | Reports without a programmer: builder, financial designer, dashboards, scheduling; ask-your-data in Arabic and English. In progress (ADR-0034, A-182, A-183): slice 9a done 2026-10-31 (the projected facts of the `reporting` schema, the catalogue of models and fields, the query compiler with scopes, filters, comparisons and calculated fields, explain and drill); slice 9b done 2026-10-31 (the report builder screen with rows, columns, values, filters, comparisons, calculated fields, pivot, charts, drill, the query shown, CSV/XLSX export, saved reports and shares); slice 9c done 2026-11-01 (the statement engine over the journal facts, the shipped profit and loss, balance sheet and trial balance that tie to the ledger, the statements screen with drill to the ledger, the designer with preview and publishing); slice 9d done 2026-11-01 (dashboards of KPI, chart, table and statement widgets run as the reader and pinned to the home page; schedules of reports and statements as CSV, XLSX or PDF emailed through the worker with their run history; the worker host registering every module); next 9e, ask-your-data. | 7.1–7.5, 7.7, 9.1 |
| 10 | Sales-rep mobile app (quotes, orders, collections, visits, offline). | new (over 3.8's PWA) |
| 11 | Customer portal with local payment gateways. | new |
| 12 | Budgets, closing and financial statements. | 6.7, 6.9, 6.10 |
| 13 | Light manufacturing: work orders over bills of material. | new |


## Trader programme (2026-11-02)

The founder's second product line (ADR-0035, A-186): the same engine with a trader edition for a small business that
imports car spare parts from China, run by a non-accountant who replaces Excel and paper. Each slice is a thin screen in
plain words over existing modules, adding to the modules whatever field or report the trader needs so the full edition
gains it too. Acceptance for every slice: the trader does the task on his own screen without an accounting term; the
postings and invariants are those of the full edition; the journey runs in English and Arabic.

| # | Work | Covers |
|---|------|--------|
| T1 | **Trader shell and purchase offers.** Done 2026-11-02: the `edition` workspace setting with its endpoints and the switch under Settings; the flat trader menu in the top bar, the drawer, the shortcuts and the palette; the Offers screen (what to buy, the suppliers, the offers with price, minimum quantity, notes, delivery and shipping, compared landed in the company's currency with the exchange rate of the day, the recommendation with its reasons and savings, the cheapest offer per item, "order from this supplier" into a purchase order); minimum order quantities and notes on quote lines, the recommendation and best-per-line in the comparison, awarding from a draft. | 4.2 (RFQ), new |
| T2 | **Shipments.** Done 2026-11-02 (A-187): the purchase order as a shipment with a dated timeline (ordered, in production, shipped, at port, customs, received), the costs along the way in their currency at the rate paid, what was paid for the goods and the effective rate, the landed cost per unit, hints (overdue, no costs, nothing paid, rate above the order's, invoice missing, stale); arrival posts the goods receipt and the landed-cost document through their services; the supplier's invoice recorded, matched and posted into the payable; an order from an offer is tracked at once. | 4.3–4.5 |
| T3 | **Pricing by exchange rate.** Done 2026-11-02 (A-188): the Trader module's pricing board over the modules' contracts: landed cost per unit from the latest shipment at the rate paid, the cost to buy again at today's spot rate (entered under Rates), the current selling price and margin, the suggested price at a target margin rounded up to an increment, alerts (selling below cost, below today's cost, margin below target, no price, no rate today, the rate's move), and applying the suggestions into the sales price list; the trader's settings kept in the organization's settings store and edited in place. | 5.2 |
| T4 | **Sales, customers and money.** Done 2026-11-02 (A-189): the one-step sale (`POST /sales/quick-sales`: order, confirm, ship, invoice, post), the Sales screen with each invoice's state in plain words and payments into a cash box or bank account, the Money screen with owed-to-you and you-owe aging, "chase first" and "pay next" in plain words and sales by month. Deferred to T5: sales by item and by customer, what to reorder. | 5.1, 5.6, 5.7, 6.1 |
| T5 | **Project health and the trader income statement.** Done 2026-11-02 (A-190): `GET /trader/health` over the modules' contracts: the period's income statement in plain words read from the ledger as the shipped profit and loss reads it (sold, returns, what the goods had cost, profit on the goods, expenses by name, bank and exchange costs, tax, what is left), the period before and six months of results; fixed costs (the trader's figure or the average of the last three months), the margin, the sales a month needs to break even, where this month leads at its pace, the cash and how long it lasts, the money position; warnings in codes worded by the screen; the shipments on the way with their money; sales by item and by customer; what to reorder from the pace of the last cover days. The Health screen in the trader menu, EN/AR. | 6.10, 7.x |
| T6 | **Stock and counting on the phone.** Done 2026-11-02 (A-191): the Stock screen in the trader menu (what the store holds, worth at cost, last moved; a count in three plain steps: start, count on the phone with the camera through the scanner at `/m/count` or type the counts on the screen, finish); `POST /inventory/counts/{id}/finish` (the uncounted lines taken as unchanged when asked, the shipped COUNT reason on every difference, review, approval and posting in one step); `QUICKER_LAN_HOST` so the phone reaches the program over the local network. Deferred: a phone shell of its own (the scanner installs as a web app), barcode label printing. | 3.x, 3.8 |
| T7 | **The trader's home.** Done 2026-11-02 (A-192): `/` in the trader edition shows the day in plain words: sold today, the month's profit or loss, the cash and its runway, owed to you and you owe with what is overdue, the shipments on the way, what to do today (the health warnings with a link each), the four quick actions and sales by month; every figure from the home and health read models. | new |
| T8 | **Everything linked: payments that move the books.** Done 2026-11-03 (A-193, A-194): paying the supplier from the shipment (an advance before the invoice, a payment of the invoice after, the dinars that left the account setting the rate), journey costs paid to the forwarder or customs and turned into their landed-cost invoices at arrival, advances applied to the invoices with the realised exchange difference, a Pay button per supplier on Money; a bank or cash line of a journal (the setup wizard's opening cash) now moves the box it names, with the harness comparing every box to all its ledger lines and a migration for the lines already posted; one test follows the whole chain from the opening cash to the health figures and ties every figure to the ledger. | new |
| T9 | **Expenses and the owner's money.** Done 2026-11-03 (A-195): the trader's Expenses screen records what the shop spends in one step through the Expenses module (paid from the cash box as a petty cash voucher from the fund made on the box, paid from the bank as the payee's bill paid the same day, or left owed for Money to pay), with the usual kinds of spend made once; the month's spend by kind; and the owner's own money put in or taken out as manual journals posted through the new `IJournals` contract against the capital and drawings accounts (settings with the chart's usual codes). | new |
| T10 | **The founder's field feedback.** Done 2026-11-03 (A-196 to A-199): (a) done: the session renewed silently, a short sale refused in the trader's words, the workspace name out of the bar; (b) done: the Offers screen rebuilt (supplier and item made from the comparison, suppliers added later, offer forms one after another, the description growing, the unit hidden, a comparison deleted); (c) done: the day's exchange rate asked for at the first open from the hour chosen, saved into the organization's rates; (d) done: the pricing board editable (any item priced from "New pricing", the cost typed on an item as the basis, the floor under every price, a price taken off the list); (e) done: the next step offered after a customer or supplier is made ("New" in the trader's bar, a customer on to the sale, a supplier on to the offers, the payment after a sale, the prices after goods arrive). | done |
| T11 | **The founder's second batch.** In progress 2026-11-03 (A-200): (a) done: the sign-out with the app open in two windows fixed, no tax on items without a tax group for the trader, what the store holds on Items, any offer ordered from, a price saved from its row with the margin live; (b) the cash box and bank account screen with money in, out and the balance, every list newest first, filters by day, month, quarter and range; (c) the trader program deployed apart from the full system with zero data and the trader edition from signup, the app on the iPhone's home screen. | new |

## Launch gate

A tenant can go live when **M1–M7 are complete and M8 slices 8.1–8.6 are complete** (workflow UI, custom-field admin, migration toolkit, webhooks/API portal, onboarding wizard with industry templates, e-invoicing where the country requires it). M9 and M10 follow; M10's performance and security work is required before scaling beyond pilot customers.

---

## M1 Foundations

Goal: a runnable, tested, multi-tenant, bilingual platform skeleton with auth, organizations, audit, numbering, currencies, the design system and the API conventions that every later module reuses.

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 1.1 Repository and toolchain | Monorepo layout (ARCHITECTURE §3), .NET solution, pnpm workspace, Docker Compose, `make up` / `make test` / `make demo`, devcontainer, GitHub Actions pipeline (format, lint, type check, unit, integration on PostgreSQL container, architecture tests, e2e, OpenAPI diff, dependency and licence audit), legacy Life OS files moved to `legacy/` (pending Q2). | `make up` on a clean machine starts everything and opens the app; CI is green on `main`; a PR with a boundary violation fails. |
| 1.2 Database foundation | `Quicker.Migrator` with versioned SQL migrations and idempotent seeds; schemas `control`/`app`/`reporting`/`ops`; RLS + forced RLS conventions; append-only triggers; composite tenant FKs; `xmin` row versions; schema tests. | Schema test proves every `app` table has `tenant_id`, RLS and policy; isolation test suite (ADR-0004) passes; migration replay test passes. |
| 1.3 Kernel | `Money`, `Quantity`, `RoundingPolicy`, `ExchangeRate`, strongly typed ids, `IClock`, `Result`, amount-in-words (EN/AR), analyzers forbidding floating point and `DateTime.Now`. | Property tests: allocation sums, UoM round-trips, Money laws; analyzer blocks a `double` in a domain project. |
| 1.4 Tenancy and identity | Tenant provisioning and suspension; users, memberships; password + TOTP + WebAuthn login; OIDC federation (Entra ID, Google) with JIT provisioning; sessions with rotating refresh tokens; API keys; step-up auth; roles, permissions catalogue, record scopes, field rules, document-type rules, SoD rules and exceptions; effective permissions. | E2E: sign up tenant, invite user, enforce MFA, sign in via OIDC; a scoped role sees only its branch in list APIs; SoD conflict blocks save with reason; permission changes appear in audit. |
| 1.5 Audit log | Hash-chained `aud_events`, EF interceptor capture, explicit events for auth and admin actions, record timeline API, tenant audit explorer, daily anchoring job, verification job. | Tampering a row in a test breaks verification; every mutation in the e2e suite produces an event with before/after. |
| 1.6 Organization | Companies, branches (auto dimension value), fiscal calendars with 12/13 periods and per-module states, currencies (ISO seed) with company overrides, rate types, effective-dated rates with provider import (ECB, Open Exchange Rates, CBI adapter), dimensions/values/sets, UoM and global conversions, business calendars and holidays, settings. | Create a company with a July fiscal year; rates for a date resolve correctly including cross rates; a due date skips Friday/Saturday in Iraq. |
| 1.7 Numbering | Series with templates, gapless and non-gapless, per company/branch/year/type, draft identifiers, audit on changes. | Parallel allocation test: 50 concurrent allocations, no gaps, no duplicates; rollback leaves no gap. |
| 1.8 Platform services | Transactional outbox and dispatcher with `LISTEN/NOTIFY`, inbox dedupe, jobs with `SKIP LOCKED`, schedules, idempotency keys, webhook subscriptions and deliveries, notifications (in-app, email via provider abstraction, Mailpit locally), attachments in S3 (MinIO), comments and activities, custom-field definitions with JSONB validation and expression indexes, saved views, document links. | Kill-the-worker test: no event lost, no duplicate effect; webhook signed and retried; a custom field defined in the UI appears in the API schema and filter. |
| 1.9 API skeleton | Minimal APIs, OpenAPI 3.1 at build, problem details with codes and `why`, cursor pagination, filter language, field selection/expansion, ETags, rate limits, generated TypeScript client, contract diff in CI. | Removing a field from a DTO fails CI; the web app compiles against the generated client only. |
| 1.10 Web shell and design system | `packages/ui` tokens and components (Radix + Tailwind), Storybook with RTL and dark variants, axe in CI; app shell with navigation, command palette, global search (basic), keyboard shortcuts overlay, data grid v1 (virtualized, column chooser, saved views, bulk select), forms with server validation mapping; i18n EN/AR with digit preference; screens for everything in 1.4–1.8. | Playwright runs the admin journeys in EN and AR; axe reports no serious violations; grid renders 100k rows smoothly. |
| 1.11 Observability | OpenTelemetry traces/metrics/logs, Serilog, health endpoints, local Grafana profile. | A request trace shows tenant, user and DB spans; health goes unhealthy on outbox lag. |
| 1.12 Demo seed v1 | Deterministic seeder: demo tenant, three companies (IQD, USD, AED), branches, users and roles, currencies and a year of rates. | `make demo` reseeds in under a minute. |

Hard scenarios proven: **18** (tenant isolation across request, report stub, export, search, jobs).

## M2 Core accounting

Goal: a chart of accounts, dimensions, the posting engine, immutable journals, periods and locks, trial balance and GL inquiry with drill-down; the invariant harness is born.

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 2.1 Chart of accounts | Charts (shared or per company), hierarchical accounts, types and categories (statement and cash-flow mapping), control flags with subledger type, dimension rules, templates (IFRS for SMEs, GCC layout, Iraq Unified Accounting System mapping), statutory mappings; import/export. | New company from template posts on day one; account with required cost centre rejects a line without one. |
| 2.2 Posting engine | `IPostingService`, posting profiles and rules with most-specific match, three-currency conversion, rounding lines, period and permission validation, control-account/subledger cross-check, append-only entries and lines, `gl_balances` increments, reversal service, rebuild command. | Unit tests over the determination matrix; a request with an unresolved role fails with the missing rule named; an unbalanced request cannot reach the database (constraint trigger test); rebuild equals incremental. |
| 2.3 Journals | Manual journals (draft → approval → post), recurring templates with schedules, reversing journals, accrual and prepayment deferral schedules, opening balance journals, attachments, journal import. | Auto-reversal posts on the reversal date via the worker; a prepayment amortises over 12 periods to the cent. |
| 2.4 Periods and locks | Per-module period states, soft/hard close, allow-posting-from/to per role, reopen with permission, reason, step-up and audit; correction posts into the first open period with links both ways. | Scenario 7 test; reopen without permission is refused; audit shows who reopened and why. |
| 2.5 Inquiry and reports | Trial balance (any date, any dimension filter, comparatives), account ledger with drill-down to source document, journal browser, balances by dimension, exports (CSV/XLSX). | Every figure on the TB drills to lines; TB totals zero at three sampled dates in tests. |
| 2.6 Invariant harness v1 | `tests/Invariants`: balanced entries, TB zero, derived equals rebuilt, audit chain, isolation; run after every scenario test and available as a job. | Harness fails when a test deliberately corrupts a balance row. |
| 2.7 Demo seed v2 | Charts per company, dimensions, a year of manual journals and accruals. | |

Hard scenarios proven: **7**, **15** (trial balance part).

## M3 Inventory

Goal: items and units, warehouses and bins, the stock ledger and costing engine, adjustments, transfers, counts and mobile scanning, with valuation equal to the GL at any date.

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 3.1 Items and UoM | Items, categories (materialised path), variants and attributes, item UoMs with exact factors and barcodes, suppliers per item, company and warehouse settings, BOMs (kits and assemblies), substitutes, images; item import. | Scenario 9 property test: 10,000 random carton/piece/dozen transactions leave zero drift. |
| 3.2 Warehouses and stock ledger | Warehouses (types) and bins, `inv_stock_ledger_entries`, `inv_stock_balances` with row locks, reservations, negative-stock policies, ATP calculation, global search of stock. | Scenario 4 (concurrency) test with two-step transfers as the outbound path; balances equal ledger sums after every test. |
| 3.3 Costing engine | Value entries, FIFO applications, daily average buckets, standard cost with variances, expected cost, cost scope, cost adjustment runs with threshold-based background continuation, GL posting through the engine, valuation report by date, "why did this cost change" screen. | Property test: any permutation of backdated inbound/outbound entries yields the same final cost as date order; INV = GL invariant at sampled dates; adjustment run explains itself with the trigger document. |
| 3.4 Adjustments, transfers, assemblies | Positive/negative adjustments with reason codes, revaluations and NRV write-downs, one-step and two-step transfers with in-transit, partial receipt with shortage handling, assembly builds. | In-transit balance equals the in-transit GL account at any date. |
| 3.5 Lots and serials | Tracking policies, expiry, FEFO suggestions, quarantine, recall with impact list, forward/backward traceability screens, serial history timeline. | Recall flags on-hand and lists shipped customers; a serial's full history is one query. |
| 3.6 Counts | Count sessions with freeze snapshots, count sheets, mobile counting, recounts, variance review and approval, posting with reason codes. | Scenario 11 test: movements posted during the count are reconciled against the snapshot. |
| 3.7 Replenishment | Reorder point, min/max, safety stock, lead times, purchase suggestions with explanation. | Suggestion explains its arithmetic. |
| 3.8 Mobile scanning v1 | PWA route group with camera scanning (BarcodeDetector, ZXing fallback), offline scan queue; flows: counts and transfers. | Playwright mobile viewport test; scanning a bin then an item records a count line. |
| 3.9 Demo seed v3 | 5,000 items with variants, lots and serials; opening stock across eight warehouses. | Demo test: 5,000 items, eight stocked warehouses, every opening unit costed and booked, invariant harness green, a reseed reproduces the same counts. |

Hard scenarios proven: **4** (adjustments/transfers path), **9**, **11**, **15** (inventory part); scenario 1's re-application mechanism is tested with adjustment documents and proven end-to-end in M4.

## M4 Procure to pay

Goal: requisitions to supplier payment with three-way match, landed costs, approvals and a reconciled AP subledger.

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 4.0 Workflow core | Definitions, rules with the safe expression grammar, steps, approver resolution, requests, actions, blocks and overrides, notifications, approvals inbox (basic UI); configured through the API and a minimal admin screen. | A rule "PO total > 10,000 USD requires finance approval" works end to end; override records who and why. |
| 4.1 Supplier master | Supplier accounts per company, groups, payment and delivery terms, posting and tax groups, WHT codes, tolerances, holds, bank accounts (encrypted). | |
| 4.2 Requisition to PO | Requisitions with approvals, RFQs with supplier invitations and quote comparison, awarding, blanket agreements with releases, purchase orders (approval, send by email with PDF, change orders with re-approval), budget commitments. | Quote comparison ranks by landed price and lead time; a PO above threshold routes to approval. |
| 4.3 Goods receipts | Receipts against POs (partial, over-receipt tolerance), lots/serials/bins, expected cost, GRNI, mobile receiving, supplier delivery note reference. | GRNI balance equals uninvoiced receipts at any date. |
| 4.4 Supplier invoices | Two- and three-way matching with tolerance rules, blocks and overrides, price/quantity variance treatment (FIFO/average adjustment, standard PPV), expense invoices, prepayment schedules, WHT at invoice, duplicate detection, capitalisation to assets (draft). | Scenario 5 test; invoice price difference on partially sold receipt adjusts Inventory and COGS correctly. |
| 4.5 Landed costs | Landed cost documents with charge types, allocation bases, estimates against clearing, charge invoices settling estimates, allocation to receipts already partly sold, cost adjustment runs. | Scenario 2 test to the minor unit; allocation report shows on-hand vs COGS split. |
| 4.6 Returns and debit notes | Supplier returns (exact cost reversing against the receipt), debit notes crediting returns or expenses, credit application to invoices. Supplier advances moved to 4.7 with bank accounts (ASSUMPTIONS A-112). | GRNI invariant extended to returns and their credits; a return at the landed cost, a note applied, a credit below cost as price variance. |
| 4.7 Payables | AP open items, aging at any date, holds, payment proposals, bank and cash accounts, supplier payments (partial, early-payment discounts, WHT at payment, charges, realised FX, on account), advances and their application. Netting with AR moves to 5.7 with receivables (ASSUMPTIONS A-113). | AP = control invariant (harness) and aging = control at two sampled dates (test); scenario 3's realised-FX mechanics exercised on the AP side (two instalments at different rates with fees). |
| 4.8 Supplier intelligence | Price history, lead-time statistics, performance scoring with configurable weights. | Scores and lead times exact on a two-supplier scenario; tolerance changes the grade. |
| 4.9 Demo seed v4 | A year of purchases including backdated receipts and late landed costs. | Eleven months of closed orders give the price history of the year before the live month; the current month posts receipts (one before the order's promised date, one after it), invoices, a landed cost two weeks after its receipt, a return with a debit note and payments; the harness passes (A-115). |

Hard scenarios proven: **1**, **2**, **5**, **15** (AP part).

## M5 Order to cash

Goal: customers, pricing, tax, quotes to receipts with credit control, shipping, invoicing, returns and a reconciled AR subledger; printable bilingual documents.

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 5.1 Customer master and CRM | Customer accounts per company, groups, sales reps and commission plans, payment/delivery terms, Customer 360 (contacts, addresses, pipeline, activities, transactions, balances), opportunities and pipeline stages. | Customer 360 loads in under 300 ms p95 on the demo tenant. |
| 5.2 Pricing engine | Price lists (derived lists, quantity breaks, currency, inclusivity), customer agreements, discount rules, promotions and bundles, floors, deterministic pipeline, "why this price" panel and API. | Scenario 14 test; property tests for determinism. |
| 5.3 Tax engine | Regimes and templates (A-004), codes and rates, groups, determination matrix UI, inclusive/exclusive, reverse charge, exemptions, WHT, tax lines on all documents, tax return report with drill-down, return period locking, UBL 2.1 export, `ITaxClearanceProvider` interface. | Tax report reconciles to output/input tax GL accounts; Saudi and UAE templates validated by test cases. |
| 5.4 Quotes and orders | Quotations, conversion, sales orders with reservations and ATP, backorders, partial cancellation, drop-ship (creates PO), credit control (exposure basis, overdue blocks, holds, release via workflow, override log). | Scenario 6 test; order confirmation under concurrent credit checks never double-passes. |
| 5.5 Pick, pack, ship | Pick lists (FEFO/bin sequence), mobile picking, packages, shipments posting COGS, partial shipments, carrier details. | Scenario 4 test on the sales path (200 parallel rounds). |
| 5.6 Invoicing | Invoices from shipments/orders (partial, consolidated), direct invoices, service lines, recurring invoices, credit notes, deferred revenue schedules, commissions accrual. | AR = control invariant; e-invoice status placeholder wired to the provider interface. |
| 5.7 Receivables | Open items, receipts with allocation (partial, overpayment, on-account, deposits and application), write-offs, realized FX with bank fees, aging at any date, statements, dunning levels and runs, promise-to-pay. | Scenario 3 realized-FX part with two instalments and fees; aging equals control at sampled dates. |
| 5.8 Returns | RMA workflow (request, authorise, receive, inspect, disposition), credit notes linked to returns at original cost, lot recall interaction, serial status chain. | Scenarios 12 and 13 tests. |
| 5.9 Print pipeline | Scriban templates, Chromium rendering in the worker, bilingual invoice/order/statement templates, email with PDF, render cache and evidence copy. | Arabic invoice renders correctly (visual regression test); draft invoices refuse to print as tax invoices. **Built 2026-10-31 (ADR-0022, A-176)**: the pipeline, the print dialog with email, WhatsApp and share links, the template screen. |
| 5.10 Demo seed v5 | A year of quotes, orders, shipments, invoices, receipts, returns across three companies and currencies. | |

Hard scenarios proven: **3** (realized part), **4** (sales path), **6**, **12**, **13**, **14**, **15** (AR part).

## M6 Finance

Goal: banking and reconciliation, cheques and PDCs, FX revaluation, fixed assets, budgets, intercompany and consolidation, closing and financial statements.

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 6.1 Bank and cash | Bank/cash accounts, bank transactions subledger, transfers (same and cross currency), charges and interest, cash position view. | Bank subledger equals bank GL at any date. |
| 6.2 Statement import and reconciliation | CSV mapping templates, OFX, MT940, CAMT.053 parsers; rule-based auto-matching; manual matching; create-from-statement; reconciliation report; unreconciled aging. | Sample statements in all four formats import and auto-match at least 80% of demo lines. **Built 2026-10-31 (A-178)**: the four parsers on sample files, saved CSV mappings, import with duplicate lines skipped, the matcher (amount within five days, cheques cleared or paid from the line, a day's movements that add up, rules that book fees and interest), matching by hand with several movements, booking from a line, excluding a line, the reconciliation report and `REC-` reconciliations, the unconfirmed aging, the `/banking/reconciliation` screen. Open: bank feeds (API connections), match-to-open-item rules (a customer transfer with no receipt), a tolerance on amounts. |
| 6.3 Cheques and PDCs | Cheque books, received and issued cheques, PDC life cycle, deposits, clearing, bounces with charges and re-charge, replacements, cheque printing template, PDC registers and maturity forecast. | Scenario 10 test; PDC control accounts equal outstanding cheques. **Built 2026-10-31 (A-177)**: receipts and payments by cheque post to the PDC accounts; deposit slips, clearing, bounce with charges and a re-charged fee (the invoices reopen), replacement, issued cheques from books, paid, returned unpaid, cancelled; registers and the maturity forecast; invariant `cheques_match_control`. Open: the cheque printing template, cheques from proposals. Statement-driven clearing and paying arrived with 6.2. |
| 6.4 Petty cash and expense claims | Funds and custodians, vouchers with attachments, replenishment, expense claims with policy checks, approval and reimbursement. | **Built 2026-10-31 (A-174)**: a confirmed expense posts its accrual and opens one payable; its payment on its own date settles it; a voucher spends the fund's cash box and a replenishment refills it, the box reconciling to its GL (harness). Open: receipt enforcement, custodian on the fund screen, per-diem rates, expense reports. |
| 6.5 FX revaluation | Runs by scope with closing rates, unrealized postings with subledger refs, auto-reversal for open items, permanent translation for bank/cash (configurable), rerun semantics. | Scenario 3 revaluation and auto-reversal part; control equals subledger during and after revaluation. |
| 6.6 Fixed assets | Categories, books (IFRS, tax), methods and conventions, acquisition from invoices/stock, depreciation runs, revaluation, impairment, transfers, disposals, register and schedules. | FA subledger equals FA control accounts; depreciation to the cent over the asset life. |
| 6.7 Budgets | Budgets with versions, lines by account/dimension/period, import from spreadsheet, budget vs actual, budget control rules raising blocks. | A requisition exceeding budget is blocked and routed. |
| 6.8 Intercompany and consolidation | Relationships, automatic mirroring with approval, IC settlement, consolidation groups, runs with closing/average rates, elimination rules, CTA, consolidated statements with drill-down to member companies. | Scenario 8 test: mirrored entries in both companies, eliminated to zero at group level. |
| 6.9 Closing | Close checklists with automated checks, shipped-not-invoiced accrual, receipts-not-invoiced review, year-end close (per dimension optional), reopening with permission and audit, opening balances roll-forward. | Scenario 16 test. |
| 6.10 Financial statements v1 | Layout engine v1 with shipped layouts (TB, P&L, balance sheet, indirect cash flow, comparatives, dimension filters), export. | Statements tie to the TB; cash flow reconciles to bank movement. |
| 6.11 Demo seed v6 | Statements, cheques, assets, budgets, intercompany sales, a closed prior year. | |

Hard scenarios proven: **3** (complete), **8**, **10**, **16**, **15** (all subledgers at any date).

## M7 Reporting platform

Goal: the reporting schema, semantic layer, report builder, financial report designer, dashboards, print designer, scheduling and exports; the 10M-line benchmark.

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 7.1 Reporting schema | Fact and dimension tables, outbox projections, rebuild and verification, read-replica routing, custom-field dimensions. | Projections land within 5 s p95; rebuild equals incremental. |
| 7.2 Semantic layer | Models and fields (EN/AR labels), compiler with security predicates, calculated fields, comparisons, drill-down to lines and documents, "show query". | Every core model has drill-through tests; a scoped user cannot widen results through a shared report. |
| 7.3 Report builder | Pivot UI (rows/columns/values), filters and parameters, calculated fields, charts (ECharts), save/share/parameterise, export, keyboard-first. | A business user builds gross margin by customer × category × rep × branch without help text. |
| 7.4 Financial report designer | Row and column designers, formulas, budget/prior/YoY columns, dimension slicing, publishing to menus, drill-down. | Shipped P&L reproduced in the designer ties to the TB. |
| 7.5 Dashboards | Widgets from saved reports, executive templates (cash position, AR/AP aging, DSO/DPO, inventory turns and aging, gross margin, budget vs actual), drill-down, refresh policies. | |
| 7.6 Print template designer | Block editor, live preview, language modes, versioning and rollback, label templates. | |
| 7.7 Scheduling and exports | Schedules with recipients and formats, conditional sending, Excel with real numbers, CSV, PDF, run history. | |
| 7.8 Benchmark and columnar decision | 10M-line tenant; six reference cubes; tuning; decision recorded in ADR-0021 (PostgreSQL stays or ClickHouse target added). | Scenario 17: under 5 s cold, 2 s warm; if not, the ClickHouse target is implemented in this milestone. |

Hard scenarios proven: **17**.

## M8 Platform depth

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 8.1 Workflow designer | Visual rule builder, delegation, escalation timers, SLA dashboard, mobile approvals inbox, email approval links. | |
| 8.2 Custom fields admin | Full admin UI (lookups, conditional visibility, print and report integration), configuration export/import. | |
| 8.3 Migration toolkit | Import templates for master data, opening balances, open AR/AP items, open orders and POs, stock with lots/serials, fixed assets; validate-then-commit runs; go-live checks (opening equity zero, subledgers equal controls). | A sample legacy export migrates and passes the invariant harness. |
| 8.4 Integration surface | Webhooks UI with test and replay, API keys UI, public API documentation portal with examples, bulk endpoints, full tenant export bundle. | |
| 8.5 Onboarding | Setup wizard, industry templates (trading/distribution, services, light assembly, wholesale-retail), sample data option, readiness checklist. | A new tenant reaches first posted invoice in under 30 minutes in a guided test. |
| 8.6 E-invoicing adapters | ZATCA phase 2 (signing, hash chain, clearance/reporting, QR), Peppol access-point integration, PDF/A-3 with embedded XML. | ZATCA sandbox compliance tests pass. **ZATCA built 2026-10-31 (A-179)**: the E-invoicing module with the ZATCA adapter behind the tax module's clearance interface (UBL 2.1 in the ZATCA profile, the invoice hash chain, the XAdES signature and TLV QR of simplified invoices, clearance of standard invoices with the scheme's QR, device onboarding through compliance and production certificates with renewal), an in-process simulator that answers like the scheme, the `/tax/einvoicing` screen and the invoice's e-invoice tab with the QR. Open: the run against ZATCA's own sandbox with a founder's OTP (the simulator holds the implementation to the scheme's rules, the sandbox is the certification), Peppol, PDF/A-3 with the embedded XML. |
| 8.7 Stretch | SAML federation, payment files (pain.001), partner portal read-only access. | |

## M9 AI layer

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 9.1 Ask-your-data | Natural language (EN/AR) to semantic query with explanation and edit/save; guardrails per ADR-0028. | Answers show the query; results equal the report builder's for the same definition. |
| 9.2 Anomaly detection | Rule-based and statistical findings queue (duplicate invoices, unusual margins, suspicious journals, price outliers, dormant suppliers). | Seeded anomalies in demo data are found; false-positive rate measured. |
| 9.3 Forecasting and reorder | Baseline forecasts, suggestion drafts with explanations. | |
| 9.4 Invoice capture | PDF/photo extraction to draft supplier invoice with confidence highlighting and matching. | Human approval remains mandatory; drafts pass through M4 matching. |

## M10 Hardening

| Slice | Delivers | Acceptance criteria |
|-------|----------|---------------------|
| 10.1 Performance | k6 suites on the 10M-line tenant; p95 budgets (A-043) met; index and query tuning; capacity guide. | |
| 10.2 Security | ASVS L2 checklist with evidence, threat models per module, external penetration test remediation, secrets and key rotation runbooks. | |
| 10.3 Operations | Backup/restore drill (documented and timed), DR runbook, on-premise installer and upgrade guide, monitoring dashboards and alert catalogue. | Restore of the demo tenant from backup verified by the invariant harness. |
| 10.4 Documentation | User guide (EN/AR), admin guide, API guide, implementation playbook. | |

## Deferred (not in v1, architected for)

Manufacturing/MRP (stock ledger production entry family reserved), HR and payroll (employee partner role reserved), point of sale (order channel reserved), e-commerce connectors (webhooks and API are the integration surface), lease accounting (IFRS 16), hedge accounting, project accounting beyond the project dimension, tax filing submissions beyond reports and export files, EDI, SMS/WhatsApp notifications (adapter slot), partner portal beyond read-only (8.7 stretch).
