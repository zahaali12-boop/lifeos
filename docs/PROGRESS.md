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
| 1.4 Tenancy and identity | done | Modules `Quicker.Tenancy` (catalogue, security policy, provisioning) and `Quicker.Identity` (users, memberships, sessions, roles, permissions, scopes, field and document-type rules, SoD, API keys, SSO). Migration `V0002__identity.sql`. Built-in auth: Argon2id passwords with policy and optional HIBP check, lockout, tenant selection, TOTP + recovery codes, WebAuthn registration/assertion (Fido2), rotating refresh tokens with reuse detection, step-up, password reset, invitations, OIDC authorization-code flow with PKCE and JIT provisioning + group→role mapping. Request pipeline: unit of work per request (middleware) with commit-on-success filter and an explicit `CommitOnFailure` for security bookkeeping; principal resolution cached by permissions epoch; `RequirePermission` and `RequireRecentAuth` endpoint filters; problem details by `ErrorKind`. Tests: 19 API-level tests (sign-up, lockout, refresh reuse, MFA, step-up, reset, invitations, SoD, API keys, cross-tenant 404s, tenant selection, full OIDC flow against an in-process fake provider) plus row factories so the schema suite covers all 9 new tenant tables. |
| 1.5 Audit log | done | Module `Quicker.Audit` and migration `V0003__audit.sql`: per-tenant hash chains in `app.aud_events` (monthly partitions, RLS, append-only; `seq`, `prev_hash` and `hash` assigned by a SECURITY DEFINER trigger under the tenant's head lock, canonical form rendered by `app.aud_canonical`), a platform chain in `control` for events recorded outside any tenant, `control.aud_anchors` and `control.aud_verifications`. EF `SaveChangesInterceptor` captures every insert/update/delete of entities annotated `HasAuditTrail` (Identity: user, membership, role, SoD rule, API key, SSO connection) with before/after/diff, redaction by annotation and by name; the sink buffers per unit of work and writes at commit; explicit events win over captured ones and inherit their diff; anonymous-then-tenant requests carry their events into the tenant. Verifier recomputes every link and checks head and anchor (`ok`/`empty`/`broken`/`truncated`/`anchor_mismatch`); file anchor store (hash-linked JSON lines); `AuditChainJobs` anchors and verifies every tenant. API: record timeline, explorer (keyset paging, filters), event detail, audited JSON-lines export, chain status/verify/anchor, operator-only platform chain. `IUnitOfWork.BeforeCommit` hook and `AddModuleDbContext` (interceptors) in the building blocks. Tests: 24 (row and anchor-file tampering, tail removal and head rewrite detected; every identity mutation produces an event with before/after; secrets absent from exports; platform chain operator-only; app role cannot alter events or heads; all-tenant jobs), and the isolation suite covers the two new tenant tables. |
| 1.6–1.12 | next | in roadmap order |

## In progress

- M1 slice 1.6 Organization (next up).

## Next

1. M1 slice 1.6: companies, branches, fiscal calendars, currencies and rate types, dimensions, units of measure (see `docs/ROADMAP.md`).
2. M1 slices 1.7–1.12 in roadmap order.

## Known gaps and interim pieces (explicit, per the working rules)

- **Audit anchoring is file-based and on demand.** `FileAuditAnchorStore` is the only `IAuditAnchorStore`; the object-lock (S3) store lands with the storage client in 1.8, and the daily anchoring/verification schedule lands with the scheduler in 1.8 (the job entry points `AuditChainJobs.AnchorAllAsync/VerifyAllAsync` exist and are tested). Verification and export run synchronously inside a request (page size 2000, export capped at 50 000 rows); long chains move to jobs in 1.8.
- **Audit retention** (dropping partitions older than the configured period, default 10 years) is not implemented; nothing deletes audit data yet.
- **Email is captured, not sent.** `CapturingEmailSender` (dev/test) is the only `IEmailSender`; the SMTP/API provider ships with the Collaboration module in 1.8. Invitation and reset links are therefore only visible in logs locally.
- **WebAuthn ceremonies are not covered by automated tests.** Registration/assertion options, storage and verification are implemented with Fido2NetLib, but no test generates a real authenticator attestation; a browser check is scheduled with the web shell in 1.10.
- **Worker host and Compose service** arrive with the outbox in 1.8.
- **Rate limiting, idempotency keys, cursor pagination and the filter language** are slice 1.9.

## How to run what exists

```
# prerequisites: .NET 10 SDK, Node 22 + pnpm, PostgreSQL 16+ (or Docker for `make up`)
make migrate            # applies db/migrations and db/repeatable to the local database
make test-dotnet        # 101 tests: kernel, schema contract, RLS isolation, append-only, migration replay, identity API, audit API and chain integrity
pnpm install && pnpm --filter @quicker/web test && pnpm --filter @quicker/tools check-diagrams
make api                # http://localhost:8080/health/ready and /api/v1/openapi.json
# sign up a workspace: POST /api/v1/auth/signup {tenantName, slug, ownerEmail, ownerName, password}
# audit: GET /api/v1/audit/events, GET /api/v1/audit/records/{type}/{id}, POST /api/v1/audit/chain/verify, POST /api/v1/audit/chain/anchor
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
