# ADR-0015: Tamper-evident audit log

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

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
* The per-tenant head lock serialises the audit writes of a tenant. Writing at commit keeps the lock short; if a tenant's write throughput ever needs more, the chain can be split into a fixed number of lanes per tenant without changing the verifier's contract.

## Amendments (M1.5, 2026-09-22): what was built

* **Tables.** `app.aud_events` (partitioned by month on `occurred_at`, RLS, append-only) with `seq` per tenant, `app.aud_chain_heads` (seq, head hash; only the chaining trigger writes it), `control.aud_platform_events` + `control.aud_platform_chain_head` for events recorded outside any tenant (A-050), `control.aud_anchors` and `control.aud_verifications` (append-only history for both chains).
* **Canonical form and hashing.** `app.aud_canonical(...)` renders the hashed fields as `quicker-audit-v1` followed by one `<octet length>:<text>` per field (NULL as `-`), LF-separated; jsonb and inet fields use their PostgreSQL text form. The `BEFORE INSERT` trigger (SECURITY DEFINER) locks the head row, assigns `seq`, sets `prev_hash` and `hash = sha256(prev_hash || canonical)`, and moves the head. The application role has INSERT only; UPDATE/DELETE are revoked and trapped by the append-only trigger (A-053).
* **Capture.** An EF `SaveChangesInterceptor` records every insert/update/delete of entities annotated with `HasAuditTrail(...)`; properties can be `AuditIgnore()`d or `AuditRedact()`ed. Events are buffered in the unit of work and inserted just before commit (A-051); explicit events win over captured ones for the same record and inherit their before/after/diff (A-052). Payloads pass a name-based redaction as a safety net (A-055).
* **Verification.** `ChainVerifier` recomputes every link from the database's rendering (contiguous `seq` from 1, `prev_hash` links, SHA-256), compares the stored head with the last link (detects a removed tail or a rewritten head) and the newest anchor with the hash at its sequence number, both the database row and the copy read back from the store. Outcomes: `ok`, `empty`, `broken` (first broken sequence and reason), `truncated`, `anchor_mismatch`; every run is stored.
* **Anchoring.** `IAuditAnchorStore` with a file implementation (append-only JSON lines, each line carrying the SHA-256 of its predecessor); object-lock storage and the daily schedule follow in M1.8 (A-054). `AuditChainJobs` anchors and verifies every tenant plus the platform chain in one call, one unit of work per tenant under a system actor.
* **API.** `/api/v1/audit/records/{type}/{id}` (timeline), `/audit/events` (explorer with keyset paging and filters), `/audit/events/{id}`, `/audit/export` (JSON lines; the export is itself audited), `/audit/chain` (+ `verify`, `anchor`, `anchors`, `verifications`), and `/audit/platform/...` for operators. Permissions `audit.event.read`, `audit.event.export`, `audit.chain.verify`, `audit.chain.anchor`.
* **Partitions.** Monthly, created two years ahead by the migration and on demand by `app.aud_ensure_partition` (SECURITY DEFINER, attach with a light lock). Retention (dropping old partitions after the configured period) is an operator job to be added with the job framework.
