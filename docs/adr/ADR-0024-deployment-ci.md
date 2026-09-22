# ADR-0024: Deployment, environments, CI/CD and one-command setup

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

One-command local setup; CI running lint, type checks and the full suite on every change; cloud SaaS and on-premise from one codebase; a solo team that cannot babysit infrastructure.

## Decision

### Artefacts

* Three container images from one build: `quicker-api`, `quicker-worker` (includes Chromium), `quicker-migrator`; the web app is built into the API image (served as static files behind the same origin) and also published as a static bundle for CDN hosting.
* Semantic versions from git tags; every image carries the commit SHA and the OpenAPI document version.

### Local development

`make up` (or `./dev up`) runs Docker Compose: PostgreSQL, MinIO, Mailpit (email catcher), the migrator (migrations + reference seed + demo tenant), API with hot reload, worker, web dev server. `make test` runs the whole suite the way CI does. `make demo` reseeds the demo tenant. A devcontainer definition gives the same environment in VS Code and cloud sessions.

### Environments

| Environment | Purpose | Data |
|-------------|---------|------|
| `local` | development | seeded demo |
| `ci` | ephemeral per pipeline | seeded demo |
| `staging` | pre-production, mirrors production topology | anonymised copy of demo plus synthetic load tenants |
| `production` | SaaS | real |
| `onprem` | customer-operated Compose or Kubernetes | real |

### SaaS topology (Kubernetes, Helm chart in `/deploy/helm`)

API deployment (HPA on CPU/latency), worker deployment, managed PostgreSQL (with PITR and a read replica), S3-compatible object storage, ingress with TLS (ACME), OpenTelemetry collector, Grafana stack (or a hosted equivalent) for logs/metrics/traces, secrets from the platform's secret manager mounted as env. Region per environment (ASSUMPTIONS Q3); tenants record their region for future multi-region routing.

### On-premise

`docker compose -f deploy/compose/onprem.yml up` with a `.env` template; includes backups (pg_dump nightly plus WAL archiving to a local or S3 target), a `quicker` CLI for migrate/backup/restore/verify, and an offline licence key. Kubernetes manifests are the same Helm chart with `mode=single-tenant`.

### CI/CD (GitHub Actions)

* On every pull request: restore caches → `dotnet format --verify-no-changes`, ESLint, Stylelint → `dotnet build` (warnings as errors), `tsc --noEmit` → unit tests → integration tests on a PostgreSQL service container → architecture tests → invariant harness and scenario suite → Playwright e2e (sharded) → Storybook axe → OpenAPI diff → dependency, licence and container scanning (Trivy, `dotnet list package --vulnerable`, `pnpm audit`) → build images (not pushed).
* On `main`: everything above, push images with the SHA tag, deploy to staging automatically, run the smoke suite and the k6 baseline against staging; production deploys on tag with manual approval; migrations run first (backward compatible), then rolling deploy of API and worker.
* Release notes generated from conventional commits; CHANGELOG kept in the repository.

### Backups and recovery

Managed PostgreSQL PITR (35 days) plus a nightly logical dump per tenant tier to object storage with object lock; restore drill scripted and executed in M10 and quarterly thereafter; recovery objectives: RPO 5 minutes, RTO 1 hour for SaaS.

## Alternatives considered

* **Serverless / PaaS-only.** Simple at first, but Chromium, long jobs and on-premise parity argue for containers everywhere.
* **Separate repositories for API and web.** One pipeline and atomic changes across the contract are worth more than repository purity.

## Consequences

* The same images serve every environment, so on-premise is not a fork.
* CI is the definition of done; nothing merges red.
