# Progress log

The single place a new session reads first (after `CLAUDE.md`). Keep it current: what is done, what is in progress, what is next, what is open.

## Status

**Phase 0 (Blueprint): approved by the founder on 2026-09-22 (defaults accepted for Q1–Q8).**

**M1 Foundations: in progress** (see `docs/ROADMAP.md`). Development environment note: this session runs on Ubuntu 24.04 with .NET 10.0.112 SDK (apt), Node 22 + pnpm, a local PostgreSQL 16 cluster and Docker (image pulls from Docker Hub are blocked by the egress policy, so tests use the `QUICKER_TEST_CONNECTION` override instead of Testcontainers here; CI uses a postgres:17 service container).

Branches: `claude/quicker-erp-founding-arch-4cq18i` (Phase 0 and slices 1.1–1.5), `claude/efficient-request-ajd9zy` (slice 1.6 onwards, built on top of it). Default branch: `main`.

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
| 1.6 Organization | done | Module `Quicker.Organization` (+ `.Contracts`, `.TestSupport`, `.Tests`), migration `V0004__organization.sql` (17 tenant tables, `control.currencies`), seed `S0001__currencies.sql` (168 ISO 4217 currencies, EN/AR names). Companies (functional/reporting currency, time zone, costing, rounding and policy settings; functional currency immutable), branches with their auto-created `BRANCH` dimension value, company currencies (display decimals, cash rounding), typed settings per tenant and per company. Fiscal calendars with 12 or 13 periods, years opened explicitly or automatically for a new company, `FY2026/27` codes, per-company per-module period states (open / soft_closed / hard_closed / never_opened) with the reopen override (reason, recent auth, audited) and the `(company, date, module)` resolver. Rate types (system + `official`/`market`), effective-dated rates with correction reasons, resolution direct → inverse → cross through the functional currency, provider import (ECB daily/90-day XML, Open Exchange Rates latest/historical) that never overwrites manual rates and is audited. Dimensions (hierarchical, validity, company scope), dimension sets deduplicated by SHA-256 hash (race-free `ON CONFLICT`), units of measure with rational conversions resolved directly, inversely or through one intermediate unit. Business calendars (working days + holidays) with due-date and working-day arithmetic. Contracts for later modules: `ICompanyDirectory`, `IFiscalPeriodResolver`, `IExchangeRateResolver`, `IWorkingDayCalendar`, `IDimensionSets`, `IUomConversions`. `ITenantSetupStep` (Tenancy contracts) lets modules seed tenant defaults at sign-up; Organization seeds calendars, dimensions, rate types and units. Tests: 17 (July fiscal year with 13 periods and date resolution, period states and reopen audit, defaults at sign-up, branch → dimension value, company currencies and settings, direct/inverse/cross rates incl. the Iraq official/market case, corrections with reasons, ECB and OXR import against a local stub, due dates skipping Friday/Saturday and holidays, dimension sets, unit conversions, provider parsers) plus row factories so the isolation suite covers all 17 tables. |
| 1.7 Numbering | done | Module `Quicker.Numbering` (+ `.Contracts`, `.TestSupport`, `.Tests`), migration `V0005__numbering.sql`: `num_series` (code, document type, company, optional branch and fiscal year, template, start number, gapless, reset never/yearly/monthly, validity, default), `num_series_counters` (one counter per series and reset period), `num_allocations` (append-only log with unique (series, period, number) and (series, document)), and `app.num_allocate` which takes the next number under the counter's row lock inside the caller's transaction. Templates: `{company} {branch} {yy} {yyyy} {mm} {fy}` and one `{seq}`/`{seq:N}`, validated at save; the sequence is rendered by the database so the stored text equals what the caller received. Selection: most specific series wins (branch > fiscal year > default), explicit override to any applicable series. `INumberAllocator` (allocate/preview) for documents, `DraftIdentifiers` (`DRAFT-xxxxxxxx`). Admin API: series CRUD (audited by capture; gapless/reset/type frozen once numbers exist), counters per period with the audited backwards move (`numbering.counter.reset` + reason, never below an issued number), allocation log, gapless audit report (missing numbers per period), preview and allocation for external documents. Tests: 18 — selection and templates, validation codes, 50 concurrent allocations consecutive with no gap or duplicate, rollback reuses the number, immutability of the log for the application role, yearly (July fiscal year) and monthly resets, counter moves with permission and audit, template engine unit tests — plus row factories for the 3 tables. |
| 1.8 Platform services | in progress | **(a) Messaging core: done.** Migration `V0006__platform.sql` (`ops.outbox_messages` with an identity sequence, `ops.inbox`, `ops.jobs`, `ops.schedules`, `ops.idempotency_keys`, NOTIFY triggers) and seed `S0002__platform_schedules.sql` (daily audit anchoring and verification, outbox archive, idempotency sweep). `Quicker.Messaging` now holds: `IOutbox` (writes integration events inside the unit of work), `IIntegrationEventHandler<T>` with a registry built from DI, the dispatcher (SKIP LOCKED claims in insertion order, one message in flight per aggregate so order holds per aggregate, handlers in their own tenant-bound unit of work with the inbox row committed alongside the effect, exponential backoff 1 s → 1 h, dead letters after 20 attempts, operator list/retry, archive), `IJobQueue` and `IJobHandler<T>` (claim with SKIP LOCKED by priority, final state written in the job's own transaction, heartbeat on a side connection, stale reclaim, `JobFailedException` for permanent failures, retries with backoff then dead, idempotency keys per tenant and type, progress reporting, tenant/operator admin), a five-field cron evaluator with time zones, the scheduler (leader by advisory lock, arms new schedules without firing the past, one job per slot by idempotency key, missed slots collapse), LISTEN/NOTIFY wake-up with polling fallback, and the hosted loops. `Quicker.Worker` host (+ `deploy/docker/worker.Dockerfile`, Compose service, `make up`); the API can embed the loops with `Quicker:Worker:Embedded=true`. Jobs registered: `audit.anchor_all`, `audit.verify_all` (fails when a chain is broken), `organization.rates.import` (per tenant), `ops.outbox_archive`, `ops.idempotency_sweep`. API: `/api/v1/platform/jobs` (list, get, types, retry, cancel), `/platform/schedules` (tenant), `/platform/ops/*` (operators: outbox by state, retry dead letters, platform schedules). Tests: 29 — kill-the-worker for events (30 events, crash mid-batch, restart: every effect exactly once, order per aggregate, redelivery a no-op), two workers sharing the queue, backoff → dead letter → operator retry, archive; jobs on three slots surviving a crash with one effect each, retry/backoff/dead/permanent/retry, idempotency keys, heartbeat and reclaim, tenant API isolation; cron semantics (Vixie OR rule, time zones, leap day), scheduler arming/firing/slot idempotency/leader lock, seeded platform schedules and the audit jobs end to end; the hosted loops woken by NOTIFY with polling disabled. **(b) next:** idempotency keys on mutating requests, webhook subscriptions and signed deliveries, notifications and the SMTP provider (Mailpit locally), attachments in S3/MinIO and the object-lock audit anchor store. **(c) after that:** comments and activities, document links, saved views, custom-field definitions with JSONB validation and expression indexes. |
| 1.9–1.12 | next | in roadmap order |

## In progress

- M1 slice 1.8 Platform services: part (a) messaging core is done; part (b) idempotency keys, webhooks, notifications/email, attachments is next; part (c) collaboration, saved views and custom fields follows.

## Next

1. M1 slice 1.8 (b) and (c) as listed in the slice table.
2. M1 slices 1.9–1.12 in roadmap order. The `gl_charts` and posting-profile foreign keys for `org_companies` arrive with M2 accounting.

## Known gaps and interim pieces (explicit, per the working rules)

- **Audit anchoring is file-based.** `FileAuditAnchorStore` is the only `IAuditAnchorStore`; the object-lock (S3) store lands with the storage client in 1.8 (b). Anchoring and verification now run daily as the platform jobs `audit.anchor_all` (02:00 UTC) and `audit.verify_all` (03:00 UTC; the job fails, and is visible as such, when any chain is broken). Verification and export from the API still run synchronously inside a request (page size 2000, export capped at 50 000 rows).
- **Audit retention** (dropping partitions older than the configured period, default 10 years) is not implemented; nothing deletes audit data yet.
- **Email is captured, not sent.** `CapturingEmailSender` (dev/test) is the only `IEmailSender`; the SMTP/API provider ships with the Collaboration module in 1.8. Invitation and reset links are therefore only visible in logs locally.
- **WebAuthn ceremonies are not covered by automated tests.** Registration/assertion options, storage and verification are implemented with Fido2NetLib, but no test generates a real authenticator attestation; a browser check is scheduled with the web shell in 1.10.
- **Rate limiting, idempotency keys, cursor pagination and the filter language** are slice 1.9.
- **Central Bank of Iraq rate adapter is not implemented.** The roadmap lists ECB, Open Exchange Rates and a CBI adapter; the first two ship (parsers covered by tests, import covered end to end against a local stub). The CBI page has no stable machine-readable feed I could verify from this environment, so the adapter is deferred rather than shipped untested; Iraq's official and market rates are entered manually or through a rate type an integration fills. Neither live feed has been called from this environment (no egress); the first production import should be watched.
- **Scheduled rate import** is available as the tenant job `organization.rates.import` (`PUT /api/v1/platform/schedules {code, jobType, cron, payload:{provider, rateType}}`); no tenant has one by default.
- **Fiscal year closing** (`status = closed`, closing entry, reopening a year) belongs to the Closing module (ADR-0026 year-end); today years are `future` or `open`, and period states are the only lock.
- **Company `chart_id` and `posting_profile_id`** are plain nullable columns until Accounting (M2) creates the tables they reference.
- **Numbering has no shipped series.** Series are admin data created through the API; each document module (M2 onwards) registers the default series its document types need when it arrives, so a fresh tenant has no series until then. Document types are free codes until the document registry exists.
- **Numbering roles.** No shipped role template grants `numbering.*` yet; the owner/admin wildcard covers it. Role templates gain numbering grants with the accounting roles in M2. The same holds for `platform.*` (jobs and schedules).
- **Outbox archive is a delete.** Published messages and their inbox rows older than 30 days are deleted by `ops.outbox_archive`; the archive partition of ADR-0010 arrives when reporting needs event history.
- **A dead letter blocks its aggregate.** Later events of the same aggregate wait until an operator retries the dead one (strict per-aggregate order); there is no "discard" action yet.
- **Embedded worker is off by default** (`Quicker:Worker:Embedded=false`); `make up` runs the separate `worker` service. The worker image is not built in CI (only the solution is built and tested there).
- **Job progress is polled**, not pushed: the server-sent events channel for progress arrives with the web shell (1.10).
- **Organization has no UI yet** (the web shell is 1.10); every capability is exposed on `/api/v1/organization` with OpenAPI summaries.

## How to run what exists

```
# prerequisites: .NET 10 SDK, Node 22 + pnpm, PostgreSQL 16+ (or Docker for `make up`)
make migrate            # applies db/migrations and db/repeatable to the local database
make test-dotnet        # 165 tests: kernel, schema contract, RLS isolation, append-only, migration replay, identity API, audit API and chain integrity, organization API, numbering API, outbox/jobs/scheduler
pnpm install && pnpm --filter @quicker/web test && pnpm --filter @quicker/tools check-diagrams
make api                # http://localhost:8080/health/ready and /api/v1/openapi.json
# sign up a workspace: POST /api/v1/auth/signup {tenantName, slug, ownerEmail, ownerName, password}
# audit: GET /api/v1/audit/events, GET /api/v1/audit/records/{type}/{id}, POST /api/v1/audit/chain/verify, POST /api/v1/audit/chain/anchor
# organization: POST /api/v1/organization/companies {code, legalName:{en,ar}, country, functionalCurrency, timeZone, fiscalCalendarId?}
#   GET  /api/v1/organization/companies/{id}/periods/resolve?date=2026-09-22&module=GL
#   PUT  /api/v1/organization/periods/{periodId}/states {companyId, modules:["GL"], state:"hard_closed"}; POST .../reopen {companyId, modules, reason}
#   POST /api/v1/organization/rates {rateType:"spot", fromCurrency:"USD", toCurrency:"IQD", validFrom:"2026-09-01", rate:1310}
#   GET  /api/v1/organization/rates/resolve?companyId=&from=EUR&to=IQD&date=2026-09-20  (direct | inverse | cross)
#   POST /api/v1/organization/rates/import {provider:"ecb"|"openexchangerates"}
#   GET  /api/v1/organization/companies/{id}/working-days?from=2026-09-24&days=1  (due date: Thursday + 1 → Sunday in Iraq)
#   POST /api/v1/organization/dimension-sets {values:{COST_CENTER: valueId, PROJECT: valueId}}; GET /api/v1/organization/uom-conversions/convert?from=&to=&value=
# numbering: POST /api/v1/numbering/series {code, documentType, companyId, template:"INV-{branch}-{yy}-{seq:6}", branchId?, gapless, resetPolicy}
#   POST /api/v1/numbering/preview | /allocate {documentType, companyId, branchId?, date, documentId}
#   PUT  /api/v1/numbering/series/{id}/counter {periodKey, nextNumber, reason?}; GET .../allocations; GET .../gaps
# platform: GET /api/v1/platform/jobs?state=&type= | /jobs/{id} | /jobs/types; POST /jobs/{id}/retry | /cancel
#   PUT  /api/v1/platform/schedules {code, jobType:"organization.rates.import", cron:"0 6 * * *", timeZone:"Asia/Baghdad", payload:{provider:"ecb"}}
#   operators: GET /api/v1/platform/ops/outbox?state=dead; POST /platform/ops/outbox/{id}/retry
# worker: dotnet run --project src/Host/Quicker.Worker  (or make up; health at http://localhost:8081/health/ready)
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
