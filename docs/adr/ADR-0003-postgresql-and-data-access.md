# ADR-0003: PostgreSQL as system of record; data access and migrations

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

The brief fixes PostgreSQL. We must decide how much of the system's guarantees live in the database, how the application talks to it, and how the schema evolves safely across hundreds of migrations.

## Decision

### Database

PostgreSQL 17 (18 once validated in CI), one cluster per environment, one database per tier (shared) or per tenant (dedicated). Schemas: `control` (tenant catalogue, global identities), `app` (tenant data), `reporting` (fact and dimension tables), `ops` (outbox, jobs, idempotency keys, migration journal).

Database-native guarantees we rely on:

* `numeric` for every amount, quantity, rate; never `float`.
* Row-level security with `FORCE ROW LEVEL SECURITY` on every `app` and `reporting` table (ADR-0004).
* Triggers that raise on `UPDATE`/`DELETE` of append-only tables (ADR-0007), plus revoked privileges for the application role.
* Check constraints for balance and sign rules (for example each journal line has exactly one of debit/credit non-zero; control-account lines carry a subledger reference).
* Deferred constraint `journal entry balanced` implemented as a constraint trigger at commit.
* Partial and covering indexes designed per query; `pg_stat_statements` on in every environment.
* Declarative partitioning by `posting_date` range for `journal_lines`, `stock_ledger_entries`, `stock_value_entries`, `audit_events` and reporting facts (yearly partitions, created ahead by the migrator).
* JSONB with GIN indexes for custom fields and translations.
* `SELECT ... FOR UPDATE` and `FOR UPDATE SKIP LOCKED` for stock balances, numbering, outbox and jobs.

### Data access

* **EF Core 10** for aggregates (documents, master data): change tracking, global query filters for `tenant_id` and soft-archived rows as a second line of defence behind RLS, optimistic concurrency via `xmin`.
* **Dapper / raw SQL** for the append-only ledgers, balance increments, reporting queries and any bulk path. Ledger writes are single `INSERT ... SELECT` statements, not entity-by-entity tracking.
* A `IUnitOfWork` per request or job that opens the transaction, sets `app.tenant_id`, `app.user_id`, `app.request_id`, and commits the outbox with the business data.

### Migrations

* Forward-only, versioned SQL files in `db/migrations` (`V0007__gl_balances.sql`), run by `Quicker.Migrator` built on DbUp, executed as the schema-owner role. Repeatable scripts (`R__views.sql`) recreate views and functions. Checksums are recorded; a modified applied migration fails the run.
* EF Core migrations are not used; the EF model is hand-mapped. A CI test loads every entity type's query and compares the EF model's tables/columns/types against `information_schema`, so drift fails the build.
* Every migration is tested by applying it to a snapshot of the previous schema with seeded data (the migration test suite), and backwards-compatible with the previous application version for rolling deploys (expand/contract pattern).
* Reference data (currencies, countries, UoM, tax templates, chart templates) lives in `db/seed` as idempotent `MERGE` scripts run by the migrator.

## Alternatives considered

* **EF Core migrations as the source of truth.** Familiar, but RLS policies, triggers, partitioning and partial indexes end up as opaque `migrationBuilder.Sql` strings, and generated diffs are hard to review. SQL-first keeps the crown-jewel schema readable and reviewable.
* **Stored-procedure-heavy design.** Rejected: business logic in C# is testable and typed; the database enforces invariants, not workflows.
* **Second database technology for search or queues.** Deferred: PostgreSQL covers both at our scale (ADR-0010, ADR-0023).

## Consequences

* The schema is the most reviewed artefact in the repository; every migration goes through the same PR checks as code.
* Rolling deploys are safe because migrations are backward-compatible for one version.
* Some duplication between SQL DDL and EF mappings, caught by the parity test.
