# ADR-0007: Append-only ledgers and derived balances

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Posted records are immutable; balances must be right in real time and rebuildable deterministically.

## Decision

### Append-only tables

`gl_journal_entries`, `gl_journal_lines`, `inv_stock_ledger_entries`, `inv_stock_value_entries`, `inv_item_applications`, `ar_settlements`, `ap_settlements`, `bank_transactions`, `fa_asset_transactions`, `audit_events`, `outbox_messages` (until archived).

Enforcement, in layers:

1. The application role has `INSERT` and `SELECT` only on these tables (no `UPDATE`, `DELETE`, `TRUNCATE`).
2. A `BEFORE UPDATE OR DELETE` trigger raises `append_only_violation` regardless of role, so even the owner role cannot modify rows by accident (migrations that must backfill use a documented, audited `SET LOCAL app.maintenance = on` escape hatch that the trigger honours and logs).
3. The few mutable columns that legitimately change after insert are modelled as **separate rows**: for example an open item's remaining balance is not a column on the invoice line but the sum of its settlement rows; a journal entry's "reversed" state is a link row (`gl_entry_links`), not an update.
4. Partitioned by `posting_date` year; old partitions can be moved to cheaper storage but never rewritten.

### Derived balances (maintained in the same transaction)

| Derived table | Grain | Source |
|---------------|-------|--------|
| `gl_balances` | company, account, fiscal period, currency (tc), dimension set hash | `gl_journal_lines` |
| `inv_stock_balances` | item, variant, warehouse, bin, lot, serial: on hand, reserved, in transit, quality-hold | `inv_stock_ledger_entries` + `inv_reservations` |
| `inv_item_costs` | company, item (or warehouse), valuation date: average unit cost, quantity, value | `inv_stock_value_entries` |
| `ar_open_items` / `ap_open_items` | document: original, settled, remaining (tc and fc) | invoice/credit rows + settlements |
| `reporting.*` facts | per reporting model | outbox projections |

Increments happen in the posting transaction under the row lock of the balance row (`INSERT ... ON CONFLICT DO UPDATE`), so a balance is never stale for even one read.

### Rebuild and verify

`Quicker.Migrator rebuild-balances --tenant X [--company Y] [--table ...]` truncates the derived table (inside a transaction, tenant-scoped) and recomputes from the append-only source. A CI job runs the rebuild on the seeded demo tenant after the full scenario suite and asserts the rebuilt tables are byte-identical to the incrementally maintained ones. A nightly job in production runs the comparison without truncating and alerts on any difference.

Dimension sets are hashed into `dimension_set_id` (a row in `gl_dimension_sets` holding the JSONB of dimension values) so balances can be grouped by any dimension combination without a column per dimension.

## Alternatives considered

* **Balances computed on the fly.** Correct but slow for aging, valuation and statements over millions of lines; also loses the "any moment" guarantee under load.
* **Event sourcing for every aggregate.** The ledgers are already event-sourced in spirit; applying it to all master data adds complexity with little benefit. Documents use ordinary rows with immutability after posting plus the audit log.
* **Soft delete flags on posted rows.** Rejected: an "is_deleted" journal line is a lie waiting to be filtered wrong.

## Consequences

* Every correction is visible history: reversals, adjustments and settlements are rows.
* Derived tables make the hot paths fast, and the rebuild proves they are honest.
* Slightly more storage, which is cheap; certainty is not.
