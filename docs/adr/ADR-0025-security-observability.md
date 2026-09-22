# ADR-0025: Security and observability baseline

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

OWASP ASVS Level 2, SSO/MFA (ADR-0014), encryption in transit and at rest, secrets outside the code, dependency scanning, plus structured logs, metrics and tracing.

## Decision

### Security controls (mapped to ASVS L2 chapters; tracked as a checklist in M10)

* **Transport**: TLS 1.2+ only, HSTS, secure cookies for the web session (SameSite=Lax, HttpOnly), CSP with nonces, no inline scripts, frame-ancestors none.
* **Input**: all requests validated against the OpenAPI schema plus domain validation; parameterised SQL only (analyzer forbids string-built SQL); file uploads type-sniffed, size-limited, virus-scanned (ClamAV in the worker, pluggable), stored outside the web root with signed URLs.
* **Output**: React escaping, no dangerouslySetInnerHTML except the sanitised print preview (DOMPurify), Excel export cells prefixed against formula injection.
* **Data at rest**: volume encryption; column encryption (AES-256-GCM with a per-tenant data key wrapped by a master key in the platform KMS or an on-premise key file) for bank account numbers, identity secrets, API keys, OIDC client secrets, e-invoicing private keys.
* **Secrets**: never in the repository; environment variables or secret manager; a pre-commit and CI secret scanner (gitleaks).
* **Dependencies**: Dependabot/Renovate, `dotnet list package --vulnerable`, `pnpm audit`, Trivy image scans; a licence allow-list check.
* **Rate limiting and abuse**: per IP for auth endpoints, per tenant and key for the API; login lockout with backoff; breached-password check on set.
* **Tenancy**: ADR-0004 tests run every build.
* **Privacy**: per-tenant data export and deletion; PII fields tagged in the model for reports and masking; data-processing records for KSA PDPL and UAE PDPL prepared in M10.
* **Security review**: threat model per module (STRIDE) maintained in `docs/security/`, penetration test before general availability (external), and the ASVS L2 checklist with evidence.

### Observability

* **OpenTelemetry** SDK in API and worker: traces for every request and job (with tenant id, company id, user id as attributes; no PII in span names), metrics (request latency histograms per endpoint, posting latency, outbox lag, job queue depth, DB pool usage, cost-adjustment durations, tenant-level request counts) and logs.
* **Serilog** structured JSON logs with correlation ids, request ids, tenant ids; log levels per namespace adjustable at runtime; sensitive fields redacted by destructuring policies.
* **Health**: `/health/live`, `/health/ready` (database, storage, migrations applied, outbox lag under threshold), `/health/deps`.
* **Dashboards and alerts**: latency SLOs (p95 300 ms), error rate, outbox lag > 30 s, dead letters, job failures, replication lag, disk, backup success, audit chain verification, invariant checker results.
* **Business observability**: a per-tenant "system health" admin page shows outbox lag, pending jobs, failed webhooks, valuation-pending items and last verification run.

## Alternatives considered

* **Vendor APM only (Datadog, New Relic).** Fine for SaaS; OpenTelemetry keeps on-premise customers free to choose their backend.

## Consequences

* Security is enforced by CI and code conventions, then verified by review and testing.
* Every request and job is traceable end-to-end, which is also how support answers "why was this slow".
