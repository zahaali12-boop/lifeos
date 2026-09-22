# ADR-0004: Multi-tenancy: shared schema with row-level security, dedicated-database tier, on-premise

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

The product is cloud SaaS and on-premise from the same codebase. Hard scenario 18 demands that no request, report, export, search or job can touch another tenant's data, proven by tests. A tenant (customer organisation) may contain many legal companies, branches and warehouses; those are business scopes, not tenancy.

## Decision

**Shared database, shared schema, `tenant_id` on every row, enforced by PostgreSQL row-level security**, with a **dedicated-database tier** that uses the identical schema.

Mechanics:

1. Every table in `app` and `reporting` has `tenant_id uuid not null` as the first column of every primary key and unique index, and as the leading column of most indexes.
2. Each table has `ALTER TABLE ... ENABLE ROW LEVEL SECURITY; FORCE ROW LEVEL SECURITY;` and a policy `USING (tenant_id = current_setting('app.tenant_id', true)::uuid) WITH CHECK (same)`. A missing setting yields no rows, never all rows.
3. The application connects as role `quicker_app`, which does not own the tables and has no `BYPASSRLS`. Migrations run as `quicker_owner`.
4. Every unit of work begins with `SET LOCAL app.tenant_id = '<id>'` inside the transaction; the value comes from the authenticated principal (JWT claim or API key), never from the request body or URL. Background jobs carry `tenant_id` in the job row and set it identically.
5. Foreign keys are composite `(tenant_id, id)` so a row can never reference another tenant's row even if RLS were bypassed.
6. Object storage keys are prefixed `tenant/<id>/`; signed URLs are minted per request under the same principal.
7. The search index, outbox, jobs, idempotency keys and audit events all carry `tenant_id` and are under RLS.
8. Tenant catalogue in `control.tenants` includes `tier` (`shared` | `dedicated`), `region`, connection routing for dedicated tenants, status (`active`, `suspended`, `deleting`) and plan/feature flags.
9. On-premise: one dedicated database, one tenant row; the same image; licence key validates the tenant.
10. Tenant deletion: soft `deleting` state, export bundle produced (no lock-in), then hard delete by a job that iterates every table; verified by a count of remaining rows equal to zero.

**Proof (runs on every CI build):**

* Schema test: every table in `app`/`reporting` has `tenant_id`, RLS enabled and forced, a policy present, and composite FKs.
* Isolation test: for each table, insert rows for tenants A and B, then under B's context assert A's rows are invisible to `SELECT`, `UPDATE`, `DELETE`, and that inserting with A's `tenant_id` under B's context fails.
* API test: fetch, update, post, export, search and run reports against tenant A's ids under tenant B's token → 404 or empty, never 403 (no existence leakage).
* Job test: a job enqueued for A executed on a worker whose previous job was B sees only A.
* Missing-context test: a query with no `app.tenant_id` set returns zero rows.

## Alternatives considered

* **Schema per tenant.** Good isolation, but thousands of schemas make migrations slow and connection pooling awkward; cross-tenant operations (plan changes, platform analytics) become fan-out jobs. Kept as a possible future tier, not needed.
* **Database per tenant for everyone.** Best isolation, highest cost and operational load for small tenants; retained only as the dedicated tier.
* **Application-level filtering only.** One forgotten `WHERE` leaks data. RLS makes the database refuse, regardless of application bugs.

## Consequences

* Slight query overhead from the RLS predicate, offset by `tenant_id` leading every index.
* Every developer (and session) must write tenant-agnostic code; the unit of work sets context, so ordinary handlers never mention tenants.
* Supporting both tiers from day one costs a routing layer (small) and buys on-premise for free.
