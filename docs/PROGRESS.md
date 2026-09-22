# Progress log

The single place a new session reads first (after `CLAUDE.md`). Keep it current: what is done, what is in progress, what is next, what is open.

## Status

**Phase 0 (Blueprint): approved by the founder on 2026-09-22 (defaults accepted for Q1–Q8).**

**M1 Foundations: in progress** (see `docs/ROADMAP.md`). Development environment note: this session runs on Ubuntu 24.04 with .NET 10.0.112 SDK (apt), Node 22 + pnpm, a local PostgreSQL 16 cluster and Docker (image pulls from Docker Hub are blocked by the egress policy, so tests use the `QUICKER_TEST_CONNECTION` override instead of Testcontainers here; CI uses a postgres:17 service container).

Branch: `claude/quicker-erp-founding-arch-4cq18i` (all Phase 0 work). Default branch: `main`.

## Done

| Date | Item |
|------|------|
| 2026-09-22 | `docs/PRODUCT_BRIEF.md`: founder's brief saved verbatim. |
| 2026-09-22 | `docs/ASSUMPTIONS.md`: 8 questions for the founder (Q1–Q8) and assumptions A-001…A-063. |
| 2026-09-22 | `docs/ARCHITECTURE.md`: principles, stack, repository layout, runtime topology, tenancy, module map, posting and costing pipelines, security, reporting, front end, conventions, quality gates. |
| 2026-09-22 | `docs/adr/`: 30 ADRs (index in `docs/adr/README.md`), all `proposed`. |
| 2026-09-22 | `docs/DOMAIN_MODEL.md`: full entity model for every module with 22 Mermaid ER diagrams (all validated with the Mermaid parser) and the cross-module invariants. |
| 2026-09-22 | `docs/POSTING_RULES.md`: account roles and the journal matrix for sales, purchasing, inventory, banking, multi-currency, fixed assets, journals, intercompany, closing, reversals and rounding; scenario-to-rule index. |
| 2026-09-22 | `docs/ROADMAP.md`: M1–M10 as vertical slices with acceptance criteria and the hard scenarios each proves; launch gate; deferred list. |
| 2026-09-22 | `docs/GLOSSARY.md`, `README.md`, `CLAUDE.md`. |

### M1 slices

| Slice | Status | Notes |
|-------|--------|-------|
| 1.1 Repository and toolchain | done | `Quicker.sln` with central package versions and analyzers, `Makefile`, `docker-compose.yml`, GitHub Actions CI (`.github/workflows/ci.yml`), devcontainer, pnpm workspace, `apps/web` bootstrap (Vite/React/TS, ESLint typed, Vitest), `tools/check-diagrams`, Dockerfiles for migrator/api/web. The worker host and its Compose service arrive with the outbox in 1.8. |
| 1.2 Database foundation | done | `db/migrations/V0001__foundation.sql` (schemas, extensions, tenant catalogue, default privileges), `db/repeatable/R__0001_app_functions.sql` (RLS/append-only/updated_at procedures, contract views), `Quicker.Migrator` (DbUp: versioned + repeatable + seed, role bootstrap), `Quicker.Persistence` (data sources, unit of work with SET LOCAL tenant session, Dapper conventions), `Quicker.Testing` (database-per-test-class from a migrated template; `IsolationRegistry` forces a row factory per tenant table), tests: schema contract, RLS isolation, append-only, migration replay, no floating-point columns, composite tenant FKs. |
| 1.3 Kernel | done | `Quicker.Kernel`: `Money`, `Currency`, `RoundingPolicy` (half-away/half-even, cash increments, largest-remainder allocation), `ExchangeRate`, `Quantity`/`UomConversion` (rational factors), typed ids (UUIDv7), `IClock`/`FakeClock`, `Result`/`Error` with `why`, `LocalizedText`, `AmountInWords` (EN + Arabic agreement rules), `TenantContext`. `Quicker.Analyzers`: QK0001 no floating point, QK0002 no ad-hoc rounding, QK0003 no ambient clock, applied to every production project. 48 tests incl. property tests. |
| 1.4–1.12 | next | in roadmap order |

## In progress

- M1 slice 1.4 Tenancy and identity (next up).

## Next

1. M1 slice 1.4: control-plane users and memberships, built-in auth (Argon2id, TOTP, WebAuthn), OIDC federation, sessions, API keys, roles/permissions/scopes/field rules/document-type rules/SoD, effective permissions.
2. M1 slices 1.5–1.12 in roadmap order.

## How to run what exists

```
# prerequisites: .NET 10 SDK, Node 22 + pnpm, PostgreSQL 16+ (or Docker for `make up`)
make migrate            # applies db/migrations and db/repeatable to the local database
make test-dotnet        # 58 tests: kernel, schema contract, RLS isolation, append-only, migration replay
pnpm install && pnpm --filter @quicker/web test && pnpm --filter @quicker/tools check-diagrams
make api                # http://localhost:8080/health/ready and /api/v1/openapi.json
```

## Open issues and decisions pending

| ID | Issue | Owner | Blocking? |
|----|-------|-------|-----------|
| Q1 | Product name and brand | founder | no (default: Quicker) |
| Q2 | Fate of the pre-existing Life OS files in this repository | founder | no (default: move to `legacy/lifeos/`) |
| Q3 | Hosting region and data residency | founder | no (design is region-agnostic) |
| Q4 | Launch vertical / pilot customer | founder | no (default: trading and distribution) |
| Q5 | Commercial model | founder | no |
| Q6 | Identity providers | founder | no (default: built-in + Entra ID + Google) |
| Q7 | Demo companies' functional currencies and rate types | founder | no |
| Q8 | Arabic digit default | founder | no |

## How to resume in a new session

1. Read `CLAUDE.md`, then this file, then `docs/ROADMAP.md` for the current milestone and slice.
2. Re-read `docs/PRODUCT_BRIEF.md` once per session; skim `docs/ASSUMPTIONS.md` for anything marked `changed`.
3. Consult the ADR that governs the area you are about to touch before writing code.
4. Work in small commits; update this file and the relevant docs in the same commit as the code.
5. At the end of a milestone, write the report to the founder (built, how to see it, test results, decisions, gaps, next) and pause for review.
