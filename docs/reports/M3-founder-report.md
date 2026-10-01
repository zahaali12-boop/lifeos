# M3 Inventory — report to the founder

Date: 2026-09-23. Branch `claude/efficient-request-ajd9zy`, 10 commits since the M2 report (one per slice, one for the web pass). No pull request has been opened; say the word and one will be.

## 1. What was built

Every slice of the M3 roadmap (`docs/ROADMAP.md`, 3.1–3.9) is done, plus the web screens for all of it. In one sentence: an item master and a stock ledger whose every movement is costed by an engine that posts to the general ledger, so the stock is worth what the books say at any date (the harness proves it after every scenario and before the demo commits), with adjustments, transfers, assemblies, lots and serials, counts, a replenishment planner, a phone scanner that works offline, and a demo tenant carrying 5,000 items and opening stock in eight warehouses.

| Slice | Delivered |
|-------|-----------|
| 3.1 Items and UoM | Items with bilingual names, types, tracking policy (none, lot, serial, both), expiry and FEFO, category tree, brands, attributes and variants, units with exact factors and barcodes, suppliers with lead times, company and warehouse settings (reorder points, min/max, safety stock, cycle-count class), BOMs, substitutes, images, CSV import/export. |
| 3.2 Warehouses and stock ledger | Warehouses of five kinds with bins in pick order, the stock ledger (`inv_stock_ledger_entries`) and balances under row locks (two users cannot both take the last unit), reservations, negative-stock policies per company and warehouse, availability, global stock search; every movement a document asks for goes through one posting engine. |
| 3.3 Costing engine | Value entries (append-only), FIFO applications that are superseded rather than deleted, daily average buckets, standard cost with variances, expected cost settled by the invoice, cost scopes per company or warehouse, cost adjustment runs with a threshold that continues in the background, GL posting through the accounting engine, valuation at any date, an explanation per entry; the seventh harness check: inventory value entries equal the inventory accounts. |
| 3.4 Adjustments, transfers, assemblies | Positive, negative, scrap and opening adjustments with reason codes (approval when the company asks), revaluations and NRV write-downs per layer with the IAS 2 cap, one- and two-step transfers with in-transit stock and shortages on receipt, assemblies valued at what their components cost. |
| 3.5 Lots and serials | Lots with expiry, FEFO suggestions, quarantine and recall with the impact list (on hand, shipped to whom), forward and backward traces; serials one per unit with a full timeline. |
| 3.6 Counts | Count sessions with a freeze snapshot and the ledger sequence at freeze, count sheets, entries (desktop or scanner), recounts, review with movement since freeze reconciled against the snapshot (hard scenario 11), a reason on every variance, approval, posting. |
| 3.7 Replenishment | A planner (on demand and nightly across tenants) that turns reorder points, min/max, safety stock and lead times into purchase suggestions, each explaining its arithmetic; accept or dismiss with a reason. |
| 3.8 Mobile scanning v1 | A phone route group (`/m`) installable as a PWA: camera scanning (the browser's decoder, ZXing as fallback, typed input for hardware scanners), counts and one-step transfers by scanning bin then item, an offline queue replayed in order when the network returns. |
| 3.9 Demo seed v3 | 5,000 items over 22 product families with barcodes, carton units, variants, lots and serials; eleven warehouses (eight stocked, three with bins, one in transit per company); opening stock posted through the engines (8,223 entries, 3,194 lots, 1,138 serials), the importer on FIFO; the harness green before the commit. |
| Web pass | Ten screens (items, warehouses, stock, adjustments, transfers, assemblies, lots & serials, counts, replenishment, valuation) in English and Arabic, right-to-left, with keyboard shortcuts and axe on every screen. |

Migrations `V0014`–`V0021` and seeds `S0006`–`S0008`; every new tenant table is covered by the schema and isolation suites.

## 2. How to see it working

```
make up          # PostgreSQL, MinIO, Mailpit, migrations, seeds, demo tenant (about six minutes the first time), API, worker, web
open http://localhost:5173   # sign in: owner@quicker.example / DemoPass2026!  (warehouse@… is the warehouse operator of the trading company)
make demo        # rebuild the demo tenant: books plus 5,000 items and their stock
```

Things worth clicking through, in this order:

1. **Valuation** (`g 9`): pick Al-Rafidain Trading; the total is what the inventory account holds. Switch to Tigris Import: the same report on FIFO layers. Change the date to last month: nothing, because the opening stock arrived this month.
2. **Stock** (`g k`): search `WATER`; open the Balances tab to see bins, lots with expiry and serials; the Ledger tab lists every movement with its source document.
3. **Items** (`g i`): search a family (`PHONE-`, `OTC-`, `MEN-`); open one to see units, barcodes, variants and the preferred supplier; create one of your own (categories and brands are managed from the same screen).
4. **Adjustments** (`g x`): a positive adjustment needs a reason code and posts straight away; set the company setting `inventory.adjustments.approval` to `required` and the same document waits for a second person.
5. **Transfers** (`g v`): ship 20 units from Baghdad main to Basra in two steps through the in-transit warehouse; the valuation shows them in transit until they are received.
6. **Counts** (`g q`): plan a count of the cold store, freeze it, enter counts (or scan them on a phone, below), review the variances, give each a reason, approve and post; the stock and the books move together.
7. **Lots & serials** (`g z`): quarantine a dairy lot with a reason and read the impact; recall it; open a phone's serial and follow its timeline.
8. **Replenishment** (`g g`): run the planner for the trading company; open a suggestion and read why it exists; accept one, dismiss another with a reason.
9. **Scanner** (`g s`, or `/m` on a phone; add it to the home screen): choose the company and warehouse, open a frozen count, scan a bin then an item (type the code if there is no camera), enter the quantity; turn the network off, capture more, turn it back on and watch the queue drain.
10. **Platform → integrity**: `POST /api/v1/platform/integrity/run` now answers with seven checks; the seventh compares every valued item with its inventory account lines.

Without Docker: `make migrate`, `make demo`, `make api`, `make web` against a local PostgreSQL 16+ (`docs/PROGRESS.md`, "How to run what exists"). For the scanner on a real phone, the API must be reachable from the phone (`Quicker__Auth__PublicOrigin` and `VITE_API_BASE_URL` on your machine's address).

## 3. Test results

* `.NET`: 227 tests (1 S3 test skipped without MinIO; CI runs it against MinIO), every one on a real PostgreSQL database. M3 added the item master, stock ledger (concurrency, hard scenario 4), costing (FIFO, average, standard, expected cost, landed cost, revaluation, a 210-line document), documents, lots and serials, counts (hard scenario 11), replenishment and the demo suites; every inventory scenario ends with the invariant harness. The demo test seeds the tenant twice (about twelve minutes) and proves the counts repeat.
* Web: 2 unit tests, the design system's 46 axe checks, 5 Playwright journeys (admin in English and Arabic, accounting, inventory, and the phone scanner on an emulated Pixel 7 with the network cut and restored) with `axe-core` on every screen.
* CI (`.github/workflows/ci.yml`): the .NET job's timeout is 45 minutes for the longer demo test; the browser job runs a `mobile` project next to `chromium`; the branch is green locally on every job.
* Demo seed: 332 journals, 5,000 items, 1,665 variants, 8,223 opening stock entries, 3,194 lots, 1,138 serials; about six minutes on a laptop; identical counts across a reseed.

## 4. Decisions made along the way (all recorded in `docs/ASSUMPTIONS.md` A-097…A-105 and the ADRs)

* **Value entries are append-only and applications are superseded, never deleted** (ADR-0008 as built): a backdated receipt or a landed cost re-applies the affected scope from that date forward and books the difference as new entries; every figure stays explainable from its rows.
* **Journals are posted per item scope as the engine walks**, so a document with many items produces one journal per item. It keeps the value entry and its journal in one step; batching per document is a candidate improvement once receipts carry hundreds of lines (M4).
* **Same-day inbound is applied before outbound; a shortfall is valued at the expected cost and re-applied when the receipt lands**, so a sale never waits for the purchase invoice.
* **Revaluations and NRV write-downs act per layer**, each layer brought to the new unit value and any recovery capped at the layer's original cost (IAS 2).
* **A serialised item moves one line per unit**; a lot's status change releases reservations and lists the impact; recalls name the customers shipped to.
* **A count freezes a snapshot and remembers the ledger sequence**, so movements after the freeze reconcile against the snapshot instead of blocking the warehouse unless the count says so; every variance carries a reason before approval.
* **The planner is a reorder-point/min-max planner**, not a forecast; incoming supply is a contract purchasing fills in M4; an accepted suggestion waits for its purchase order.
* **The scanner is the web app installed as a PWA**, on the same session and permissions; a capture sets the counted quantity of its line, the queue replays strictly in order, and a rejected capture waits for a person without blocking the rest.
* **The demo seed's budget is ten minutes** (it takes six) because the 5,000 items and their stock go through the same services and engines a user's would; a bulk load refreshes planner statistics mid-transaction (`app.refresh_statistics()`), which took the harness from a minute to a second.
* **OpenAPI schema names are unique across modules and a collision fails the build** (ADR-0012 amended); a corrected contract records what it removed and why, and the breaking-change check accepts exactly those lines.
* Four defects found by the tests and fixed on the way: the bins list failed with an untranslatable comparer; the costing drain guard counted distinct scopes rather than re-walks of one scope, so any document with more than 200 items failed; a bulk load left the planner with empty-table statistics; the item categories were published with the account categories' schema.

## 5. Known gaps (the full list with reasons is in `docs/PROGRESS.md`)

* Costing: no landed-cost documents yet (M4 4.5 brings them; the engine already takes an inbound cost adjustment), no per-warehouse standard cost, no cost roll-up for assemblies from BOM standards.
* Replenishment: purchase orders and incoming supply arrive with M4; suggestions are per item and warehouse (no transfer suggestions, no supplier minimums or pack rounding).
* Counts: one approver step (the workflow engine is M4 4.0/M6); a blocking count blocks the whole warehouse or its bins.
* Scanner: camera decoding is best-effort and not covered by the browser journey (which drives the typed input); GS1 application identifiers are not parsed; the transfer flow is one-step without bins, lots or serials; no background sync or push.
* Demo: opening stock only, no movements after the opening date (M4–M5 seeds add documents), no BOMs, substitutes or images; suppliers are still the party references of v2.
* Web: the item screens show units, barcodes, variants and suppliers but do not edit them yet; revaluations, reservations, standard costs, inbound cost adjustments and the per-entry cost explanation have no screen; document lines take item codes (no picker); the transfer receive takes full quantities; ⌘K search covers companies only.
* The seed takes about six minutes; the same per-line cost (about 25 ms, mostly round trips) applies to large documents until the engine batches its journals.

## 6. What you can do now

1. **Review and merge**: ask for a pull request from `claude/efficient-request-ajd9zy` to `main` (none exists yet).
2. **Decide the adjustment approval policy** per company (`inventory.adjustments.approval` = `required` or not) and who may approve (`inventory.adjustment.approve`); the default posts on submit.
3. **Try the scanner on a phone** against your machine's API; the offline queue is the part worth testing on a real network.
4. **Q9** in `docs/ASSUMPTIONS.md` (Iraq statutory code list) is still open; nothing built depends on it.

## 7. What is next

M4 Procure to pay in roadmap order: the workflow core (definitions, rules, approvals inbox), the supplier master, requisitions to purchase orders, goods receipts against orders with expected cost and GRNI, supplier invoices with two- and three-way matching, landed costs settling the estimates (hard scenario 2), returns and advances, payables with aging and payments (AP equals its control account through the harness), supplier intelligence, then demo seed v4 with a year of purchases. You asked me to continue through the milestones, so I continue with 4.0 unless told otherwise.
