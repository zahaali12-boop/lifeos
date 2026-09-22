# ADR-0009: Concurrency, locking and idempotency

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Two users selling the last unit must not oversell (scenario 4); document numbers must never duplicate or gap where gapless is required; retried API calls must not post twice; posting must stay fast under concurrent users.

## Decision

### Isolation level and locking

* Default `READ COMMITTED` with **explicit row locks** on the rows that carry invariants. Serializable isolation is not used globally (retry storms under load); it is used for the few operations that touch unbounded sets (consolidation runs, year-end close), which also take an advisory lock on the company.
* **Lock order** is fixed and documented to prevent deadlocks: document header → numbering series → partner credit row → stock balance rows (ordered by item, warehouse, bin, lot, serial) → item cost scope → gl_balances rows (ordered by account, period, dimension set). A helper `LockLadder` enforces the order in code.
* **Stock**: `SELECT ... FOR UPDATE` on `inv_stock_balances` rows for every line before checking availability; under FIFO the inbound layers being applied are locked with `FOR UPDATE` in (posting_date, sequence) order. The second concurrent seller blocks, then sees zero available and gets a `StockUnavailable` error with the "why" (on hand, reserved, requested), or the negative-stock policy applies.
* **Reservations** are rows with quantity; available = on hand − reserved − quality hold; a reservation converts to an issue in the same transaction as the shipment.
* **Numbering**: the series row is locked `FOR UPDATE` and incremented inside the posting transaction (ADR-0016).
* **Credit limit**: the customer account row is locked while exposure is computed and the order confirmed, so two orders cannot both pass the check.
* **Balances**: `INSERT ... ON CONFLICT DO UPDATE` under the row lock; short critical sections.
* **Cost scope lock** for re-application (ADR-0008).
* Long jobs never hold row locks across user-visible waits; they claim work with `SKIP LOCKED` and commit in batches.

### Optimistic concurrency for edits

Every mutable row exposes a `row_version` (PostgreSQL `xmin` mapped through EF Core) surfaced as an `ETag`; updates require `If-Match`; a mismatch returns 412 with the current representation so the UI can show a merge prompt.

### Idempotency

* Every mutating HTTP request carries an `Idempotency-Key` (UUID). The key, principal, tenant, request hash and final response are stored in `ops.idempotency_keys` for 24 hours. A replay with the same key and hash returns the stored response; a replay with a different hash returns 422.
* Posting a document is additionally idempotent at the domain level: `document.status = posted` is checked under the header row lock, so even without the header, a document cannot post twice.
* Outbox consumers and webhook receivers get an `event_id`; handlers record processed ids in `ops.inbox` and ignore duplicates (at-least-once delivery, exactly-once effect).
* Import jobs use natural keys plus a per-file idempotency key so re-uploading a file does not duplicate rows.

### Timeouts and retries

* `statement_timeout` 15 s for interactive requests, `lock_timeout` 5 s; on lock timeout the API returns 409 with a retry hint. Background jobs use longer timeouts.
* Serialization failures (`40001`) and deadlocks (`40P01`) are retried up to 3 times with jitter inside the unit of work; the retry is transparent to the caller because the whole transaction re-runs.

### Proof

* Scenario 4 test: two API clients post shipments for the last unit in parallel 200 times; exactly one succeeds each round; stock balance equals ledger sum after every round.
* Numbering test: 50 parallel postings produce 50 consecutive numbers with no gaps or duplicates; a failed posting leaves no gap in gapless series (the number is allocated only on commit).
* Idempotency test: the same request replayed concurrently returns one posted document.

## Alternatives considered

* **Serializable everywhere.** Simplest reasoning but unacceptable retry rates on hot rows (balances, series).
* **Application-level distributed locks (Redis).** Extra infrastructure and a second source of truth; PostgreSQL row and advisory locks are transactional and sufficient in a modular monolith.
* **Optimistic stock checks with compensating reversals.** Produces "sorry, we oversold" flows; the brief forbids overselling.

## Consequences

* Correctness lives in the database transaction; the lock ladder must be followed by every posting path (reviewed in PRs and exercised by the scenario suite).
* Predictable behaviour under contention: the loser waits briefly and gets a clear error.
