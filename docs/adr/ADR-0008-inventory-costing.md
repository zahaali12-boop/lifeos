# ADR-0008: Inventory costing: quantity and value entries, FIFO applications, backdating

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Costing must support FIFO, moving weighted average and standard cost, configurable per company or item category; backdated transactions, late supplier invoices and late landed costs must recalculate costs and adjust the GL with a traceable reason (hard scenarios 1, 2); inventory valuation must equal the inventory GL balance at any date (scenario 15); and all of this in real time.

## Decision

Separate **quantity** from **value**, and make value corrections **additive rows** rather than edits. This is the model proven in Microsoft Dynamics 365 Business Central (item ledger / value entries / item application entries), refined for real-time operation.

### Tables (module `Inventory`)

* `inv_stock_ledger_entries` (SLE): one row per movement: item, variant, warehouse, bin, lot, serial, `entry_type` (purchase_receipt, purchase_return, sale_shipment, sale_return, transfer_out, transfer_in, positive_adjustment, negative_adjustment, count_variance, assembly_consumption, assembly_output, consignment_in, consignment_out, drop_ship), `quantity` (signed, base unit), `posting_date`, `sequence` (global order within tenant), source document, `remaining_quantity` (for inbound entries under FIFO: how much is still unapplied), `ownership` (own | consigned_in | consigned_out), `cost_is_expected` flag until invoiced.
* `inv_stock_value_entries` (SVE): item, SLE, `posting_date`, `value_type` (direct_cost, indirect_cost, expected_cost, expected_cost_reversal, revaluation, variance, rounding, cost_adjustment), `valued_quantity`, `unit_cost` (unrounded), `cost_amount_actual`, `cost_amount_expected`, `gl_journal_entry_id`, `adjusts_sve_id`, `reason` (structured: trigger document, rule), `cost_posting_group`.
* `inv_item_applications`: `outbound_sle_id`, `inbound_sle_id`, `quantity`, `is_reapplication`, `superseded_by`. Under FIFO, an outbound entry is applied to the oldest inbound entries with remaining quantity by (posting_date, sequence) within its cost scope.
* `inv_item_costs`: per (company, item, [warehouse], valuation_date): average unit cost, on-hand quantity and value at that date (average-cost buckets).
* `inv_cost_adjustment_runs`: log of each adjustment run (trigger, scope, entries touched, GL entries created, duration).

### Cost scope

Configurable per company (`costing_scope = company | warehouse`). FIFO layers exist per (company, item, [warehouse], lot, serial); average cost is per (company, item, [warehouse]). Costing method per company with override per item category and per item (`fifo | average | standard`).

### Method behaviour

* **FIFO**: outbound value = sum of applied inbound layers' unit cost × applied quantity. Layer cost changes (late invoice price difference, landed cost) create `cost_adjustment` value entries on the outbound entries in proportion to applied quantity.
* **Moving weighted average with a daily average period**: the average unit cost for a day is the value/quantity of the opening balance plus that day's inbound entries; all outbound entries of that day use it. A backdated entry recomputes averages from that day forward (only for the affected item and scope) and posts the difference as `cost_adjustment` value entries. This gives deterministic, order-independent results, unlike "true" per-transaction moving average, and is what leading ERPs implement.
* **Standard cost**: inbound entries valued at the standard; the difference to actual is posted as a `variance` value entry to the purchase price variance account at receipt (and adjusted on invoice). Standard cost versions have effective dates; a revaluation entry posts when the standard changes for on-hand stock.

### Backdating and re-application (the heart of scenarios 1 and 2)

When a value-affecting event lands at a posting date earlier than existing entries for the same item and scope (a backdated receipt, a late supplier invoice with a price difference, a landed cost document, a revaluation, a backdated shipment):

1. The engine takes a row lock on the item's cost scope (`inv_item_cost_locks`).
2. FIFO: it invalidates applications from the earliest affected date forward (marking them `superseded_by`), re-applies outbound entries in (posting_date, sequence) order against layers with remaining quantity, and computes each outbound entry's new cost.
3. Average: it recomputes daily buckets from the earliest affected date forward.
4. For every outbound (or transfer, or return) entry whose value changed, it inserts a `cost_adjustment` value entry with the delta and a `reason` naming the trigger document, and requests GL posting: Dr/Cr Inventory vs the entry's original offset (COGS for shipments, adjustment account for adjustments, transit for transfers, and so on, resolved by the posting profile).
5. The GL posting date for an adjustment is the later of the affected entry's posting date and the first open inventory period start; if the original period is closed, the adjustment posts in the first open period and the value entry keeps the original valuation date, so valuation-by-date and GL-by-date still agree (both report the adjustment in the period where it posted).
6. If the number of entries to re-apply exceeds a configurable threshold (default 2,000), the run continues in an immediate background job; the item is flagged `valuation_pending` and the UI shows a badge; when it completes, the flag clears and the run is logged.

### Expected cost (uninvoiced receipts) and negative stock

* A receipt without an invoice is valued at the PO price as **expected cost**. On invoice, an `expected_cost_reversal` and a `direct_cost` entry replace it. The GL posts expected cost to Inventory against Goods Received Not Invoiced (accrual) so valuation equals GL at every moment.
* Negative stock: blocked by default. When allowed, an outbound entry with no available layer is valued at the expected cost (last cost, else standard, else zero with a warning) and flagged `costed_at_expected`; the next inbound entry triggers re-application and adjustment.

### Returns and cost

* Sales return: restocked at the **original shipment's cost** (found through the linked shipment line and its applications), configurable to current cost; the credit note reverses revenue and tax; COGS reverses by the same value entries (scenario 12).
* Purchase return: relieves stock at the original receipt's cost (applied to that specific inbound entry, "exact cost reversing"), so the GRNI/AP side matches.

### Valuation reports

Inventory valuation at date D = Σ `cost_amount_actual` (+ expected where reported) of value entries with `posting_date ≤ D`; the GL inventory balance at D uses the same entries' journal lines. The invariant test compares them per company and warehouse posting group.

## Alternatives considered

* **Mutable cost columns on stock rows with periodic "recost" batch.** How many ERPs work (and the reason overnight runs exist). Violates real time and immutability.
* **Perpetual per-transaction moving average.** Order-dependent; backdating produces different results depending on entry order. Daily buckets remove the ambiguity.
* **Cost stored only on documents.** Cannot express late costs or backdating.

## Consequences

* Every cost figure is a sum of immutable rows with reasons; the "why did COGS change" screen is a query, not forensics.
* Re-application is the most intricate code in the system; it is isolated in one service with property-based tests (random sequences of backdated entries must yield the same cost as posting them in date order) and the hard-scenario tests.
* The threshold-based background continuation keeps the posting call fast while preserving correctness.
