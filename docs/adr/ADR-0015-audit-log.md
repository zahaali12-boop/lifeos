# ADR-0015: Tamper-evident audit log

Status: proposed · Date: 2026-09-22

## Context

Who changed what, when, from what to what, for every record, with evidence that the log itself was not altered.

## Decision

* `audit_events` (append-only, partitioned by month): `id` (UUIDv7), `tenant_id`, `occurred_at`, `actor` (user, API key or system job), `actor_ip`, `user_agent`, `request_id`, `correlation_id`, `company_id`, `entity_type`, `entity_id`, `entity_display` (number/code at the time), `action` (created, updated, state_changed, posted, reversed, approved, rejected, override, login, permission_changed, exported, printed, viewed_sensitive), `before jsonb`, `after jsonb`, `diff jsonb` (field-level changes with old/new), `reason` (mandatory for overrides, reopenings, manual rate changes, credit releases), `prev_hash`, `hash`.
* **Hash chain per tenant**: `hash = SHA-256(prev_hash || canonical_json(row without hash))`. The writer takes the tenant's chain head under a lock (`audit_chain_heads` row) so the chain is linear. A verification job recomputes the chain per partition and reports the first broken link, if any.
* **Anchoring**: every day the chain head hash for each tenant is written to object storage with object lock (WORM) and, for on-premise, to a local append-only file; the verification job compares.
* **Capture**: EF Core interceptor captures before/after for tracked entities; the posting engine, workflow engine and identity module write explicit events for actions that are not simple row changes. Reads of sensitive data (bank details, salaries if ever, full export) are logged as `viewed_sensitive`/`exported`.
* **Redaction**: secrets never enter the log (hashed or masked fields are declared per entity).
* **Access**: an "Audit" screen per record shows its timeline; a tenant-wide audit explorer filters by actor, entity, action and date, with export; retention is configurable (default 10 years) and deletion is impossible from the UI.
* **Performance**: the log is written in the same transaction (correctness over speed); partitions keep indexes small; JSONB diff avoids full snapshots for large documents (the `before`/`after` are stored for state changes, `diff` for updates).

## Alternatives considered

* **External immutable log service.** Adds infrastructure; object-lock anchoring gives equivalent evidence cheaply.
* **Database triggers writing the log.** Captures changes but not actors, reasons or business actions; used as a safety net for tables edited by maintenance scripts only.
* **Asynchronous logging.** Faster, but an audit event that can be lost is not an audit event.

## Consequences

* Every override, reopening and correction carries a reason and a verifiable place in the chain.
* The record timeline is a core UI feature, not an afterthought.
