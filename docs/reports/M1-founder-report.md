# M1 Foundations — report to the founder

Date: 2026-09-22. Branch `claude/efficient-request-ajd9zy` (on top of `claude/quicker-erp-founding-arch-4cq18i`), 21 commits since the approved blueprint. No pull request has been opened; say the word and one will be.

## 1. What was built

Every slice of the M1 roadmap (`docs/ROADMAP.md`, 1.1–1.12) is done. In one sentence: a multi-tenant, bilingual, audited platform on which the accounting and trading modules of M2–M6 can be built without revisiting the foundations.

| Slice | Delivered |
|-------|-----------|
| 1.1 Repository and toolchain | Monorepo (.NET 10 solution, pnpm workspace), `make up` / `make test` / `make demo`, Docker Compose (PostgreSQL, MinIO, Mailpit, migrator, API, worker, web), devcontainer, GitHub Actions (format, analyzers, unit/integration on PostgreSQL, schema and architecture tests, web lint/type/tests, e2e, OpenAPI contract gates, docs checks). |
| 1.2 Kernel | Exact `Money`/`Quantity`/`ExchangeRate` with an explicit `RoundingPolicy`; Roslyn analyzers that fail the build on floating point, ad-hoc rounding or ambient clocks in domain code; typed ids; `Result`/`Error` with "why" facts. |
| 1.3 Database foundation | DbUp migrator (versioned, repeatable, seeds), composite `(tenant_id, id)` keys, row-level security forced on every tenant table, append-only triggers, `updated_at` triggers, owner/app roles, per-test databases cloned from a migrated template. |
| 1.4 Tenancy and identity | Sign-up, login with lockout, refresh-token rotation with reuse detection, TOTP and WebAuthn MFA, tenant selection, invitations, password reset and policy, roles from templates with permission wildcards, scoped assignments (company/branch/warehouse), segregation-of-duties rules with exceptions and report, API keys, OIDC SSO, platform operators. |
| 1.5 Audit | Hash-chained, partitioned, append-only audit log with automatic change capture (before/after, redaction), explorer API, daily anchoring (file or S3 object lock) and verification jobs, export. |
| 1.6 Organization | Companies with functional and reporting currencies, branches (each a dimension value), fiscal calendars with per-module period states (open, soft-closed, hard-closed, reopen with reason), business calendars and working-day arithmetic, dimensions and dimension sets, units of measure and conversions, currencies, rate types, rates with direct/inverse/cross resolution and provider import (ECB, Open Exchange Rates), tenant and company settings. |
| 1.7 Numbering | Series with templates (`INV-{branch}-{yy}-{seq:6}`), gapless allocation under a row lock, reset policies, counter corrections with audit, gap report. |
| 1.8 Platform services | Transactional outbox and dispatcher, inbox de-duplication, jobs with `SKIP LOCKED`, retries and dead-lettering, cron scheduler, operator endpoints; idempotency keys; outbound webhooks with HMAC signatures, replay and secret rotation; notifications with preferences, email log and SMTP; object storage (filesystem/S3) and attachments; comments with mentions, activities, document links, saved views, custom fields with JSON Schema validation and indexed queries. |
| 1.9 API conventions | OpenAPI 3.1 generated at build and committed (`contracts/openapi-v1.json`), typed TypeScript client, CI gates for drift and breaking changes, rate limits, cursor paging, a filter language (`country eq 'IQ' and cf.region eq 'north'`), field selection, expansion, ETags with `If-None-Match`/`If-Match`. |
| 1.10 Front end | `@quicker/ui` design system (tokens, Radix components, Storybook with LTR/RTL and light/dark, axe on every story) and `@quicker/web` (TanStack router/query/table, i18n EN/AR with ICU, RTL through logical CSS only, Eastern Arabic digits, command palette, keyboard shortcuts, data grid, admin screens for companies, rates, members, roles, custom fields, notifications, audit, jobs, webhooks), Playwright journeys in both languages with accessibility checks. |
| 1.11 Observability | Structured JSON logs, OpenTelemetry traces and metrics (tenant/user/actor tags, job and event spans, outbox and job gauges), `/health/live|ready|deps`, a Grafana/Tempo/Prometheus/Loki profile (`make observe`). |
| 1.12 Demo seed v1 | `make demo`: the `demo` tenant with three companies (IQD, USD, AED functional), five branches, ten users covering every default role, official/market rate types and a year of daily rates, rebuilt from scratch in about six seconds with stable identifiers. |

## 2. How to see it working

```
make up          # PostgreSQL, MinIO, Mailpit, migrations, seeds, demo tenant, API, worker, web
open http://localhost:5173   # sign in: owner@quicker.example / DemoPass2026!  (accountant@… opens the Arabic UI)
make demo        # rebuild the demo tenant at any time
make observe     # the same plus Grafana on http://localhost:3000 (admin/admin)
```

Things worth clicking through: switch the language (the whole shell mirrors, digits follow the user's preference), the companies grid with a filter, rates for USD/IQD (spot, market and official differ as they do in Iraq), members and roles (try assigning a second role that conflicts: the SoD rule answers), the audit explorer (every change made through the API is there with before/after), jobs and webhooks under platform.

Without Docker: `make migrate`, `make demo`, `make api`, `make web` against a local PostgreSQL 16+ (`docs/PROGRESS.md`, "How to run what exists", lists every endpoint family with example calls).

## 3. Test results

* `.NET`: 185 tests across kernel, schema contract (every tenant table checked for RLS, keys, append-only and audit conventions), migration replay, identity, audit chain integrity, organization, numbering, outbox/jobs/scheduler, webhooks and idempotency, notifications and email, storage and attachments, collaboration, paging/filtering/ETags, tracing and health, and the demo seeder. Locally 184 pass and 1 (S3 object lock) is skipped without MinIO; CI runs it against MinIO. Every integration test runs on a real PostgreSQL database.
* Web: unit tests (vitest), the design system's axe suite on every story, Playwright journeys in English and Arabic with `axe-core` on each page.
* CI (`.github/workflows/ci.yml`): `dotnet` (format, analyzers, tests with PostgreSQL 17 and MinIO), `web` (lint, stylelint with logical-properties rule, type check, tests, build, i18n completeness, client and contract drift), `e2e` (migrator, API, Playwright), `docs` (diagram and link checks). The branch is green locally on all of them; CI on GitHub runs on push.

## 4. Decisions made along the way (all recorded in `docs/ASSUMPTIONS.md` A-064…A-087 and the ADRs)

* **Every tenant table is `(tenant_id, id)` with forced RLS**, including for the owner role; tests prove a wrong tenant sees nothing.
* **Audit is a hash chain per tenant** with daily anchors; the file store is the default, S3 object lock is one setting away.
* **Period control is per module** (GL, AR, AP, inventory…), with soft close and audited reopen, so month-end can close sales before purchases.
* **Rates resolve direct, then inverse, then cross through the company's functional currency**, with `official` and `market` as first-class rate types for Iraq (Q7 default).
* **Gapless numbering is a row lock, not a sequence**, so audited gaps are impossible and resets are explicit.
* **Integration goes through an outbox**; nothing is published from inside a request.
* **The API contract is generated from code and committed**; a breaking change fails CI (allowed once before the first release tag, and it was used once: lists became page envelopes).
* **RTL is CSS-logical only**, enforced by stylelint; no `left`/`right` in the code base.
* **The accountant template no longer grants `accounting.*`** — the default SoD rule (post journals versus reopen periods) made it unassignable; it now names the accounting areas and period management without reopen.

## 5. Known gaps (the full list with reasons is in `docs/PROGRESS.md`)

* Rate limits and idempotency are in-process per node; a shared store is needed before running several API nodes.
* Email is SMTP only; no provider APIs or bounce handling yet.
* Attachments are not malware-scanned and are authorised tenant-wide until documents carry record permissions (M2+).
* WebAuthn is implemented but not covered by an automated authenticator test.
* Audit retention (dropping old partitions) is not implemented; nothing deletes audit data.
* About sixty multi-branch endpoints lack typed response schemas in the OpenAPI document (they are generated as `object`), so the TypeScript client types them loosely; they are tightened as each screen needs them.
* The demo tenant is purged physically on reseed; that path exists only for the `demo` slug. Tenant offboarding with retention is a later platform slice.
* Telemetry thresholds (5 minutes outbox lag, 1000 overdue jobs) are guesses until real load.

## 6. What you can do now

1. **Review and merge**: ask for a pull request from `claude/efficient-request-ajd9zy` to `main` (none exists yet).
2. **Production settings** when you deploy: `Quicker:Email:Provider=smtp` (host, port, credentials), `Quicker:Storage:Provider=s3` (Compose already sets it), `Quicker:Api:AllowedOrigins` for the web origin, `OTEL_EXPORTER_OTLP_ENDPOINT` for telemetry, and your own `Quicker:Auth:SigningKey` / `SecretProtectionKey` (the checked-in ones are development keys).
3. **Open questions** Q1–Q8 in `docs/ASSUMPTIONS.md` still run on their defaults; answering them changes nothing already built but shapes M2–M6 (launch vertical, tax regime, official versus market rates, e-invoicing).
4. **Demo access**: every demo user shares `DemoPass2026!`; if the demo is exposed outside your team, reseed it on a schedule or change the constant in `DemoData`.

## 7. What is next

M2 Accounting core, in roadmap order: charts of accounts per company, posting profiles, journals and the append-only ledger with rebuildable balances, dimensions on lines, period-end and revaluation, then demo seed v2 (a year of journals). The `org_companies` foreign keys to charts and posting profiles arrive with it. I pause here for your review unless told to continue.
