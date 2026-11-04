# ADR-0024: Deployment, environments, CI/CD and one-command setup

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

One-command local setup; CI running lint, type checks and the full suite on every change; cloud SaaS and on-premise from one codebase; a solo team that cannot babysit infrastructure.

## Decision

### Artefacts

* Three container images from one build: `quicker-api`, `quicker-worker` (includes Chromium), `quicker-migrator`; the web app is built into the API image (served as static files behind the same origin) and also published as a static bundle for CDN hosting.
* Semantic versions from git tags; every image carries the commit SHA and the OpenAPI document version.

### Local development

`make up` (or `./dev up`) runs Docker Compose: PostgreSQL, MinIO, Mailpit (email catcher), the migrator (migrations + reference seed + demo tenant), API with hot reload, worker, web dev server. Without `make` (Windows), the same three Compose commands run by hand (README "Run it"); the migrator image takes its verb as the container command (`docker compose run --rm migrator demo`), the API's host port is `QUICKER_API_PORT`, `QUICKER_LAN_HOST` is the machine's address on the local network when a phone on the same Wi-Fi should open the app and count stock (the web app then calls the API by that address and the API accepts that origin; ADR-0035 §9), and the web container keeps the image's installed packages under anonymous volumes so a host checkout without `node_modules` still starts. A Windows install keeps itself current with `deploy/windows/setup-auto-update.bat` (once): a Git checkout of the working branch and a hidden scheduled task that runs `update.bat` at sign-in and every 30 minutes, which fetches, and only when something new arrived resets to it and runs `docker compose up -d --build` (the migrator runs before the API); `.gitattributes` keeps LF endings on every machine so the Windows checkout builds the same bytes as CI, and the command scripts are stored with CRLF. `make test` runs the whole suite the way CI does. `make demo` reseeds the demo tenant. A devcontainer definition gives the same environment in VS Code and cloud sessions.

**Amended 2026-11-03 (a founder install that stayed down).** Every long-running Compose service (PostgreSQL, MinIO, Mailpit, API, worker, web) has `restart: unless-stopped`: a part that stops on its own (a crash, memory running out on an 8 GB machine while an update builds, Docker Desktop or Windows restarting) is started again by Docker, where before it stayed down until the next update rebuilt it, and the scheduled update exits when nothing new arrived. The migrator stays a one-shot. Known gap, deferred: the Windows update script does not retry a build that failed until something new is pushed; changing that script needs care because Windows reads a running batch file from disk while the update replaces it, so it is to be split into a stable launcher and a copied worker script.

**Amended 2026-11-03 (a second installation beside the first).** The trader program is installed as a separate Compose project from the same checkout (`docker-compose.trader.yml`, project `quicker-trader`, ADR-0035 §19): its own containers, volumes and ports, a migrator that prepares the database without the demo workspace (`prepare`), and its own scheduled task (`setup-trader.bat`, `update-trader.bat`; `register-task.ps1` takes the task name and the script). One checkout means one branch for both: each task fetches and resets the checkout and rebuilds its own stack, and the lock file is shared so the two builds never run at the same time on one machine: a forced trader update (the one-time setup) waits for the other build to finish instead of giving up, and the half-hourly run builds the trader stack whenever it has never been built (no API container yet), so a setup that ran while the full system was building still ends with the stack up. The two stacks never share a database, a volume or a port. The trader stack's web container serves the built app with nginx (`deploy/docker/nginx.conf`, the `static` target of `web.Dockerfile`), which proxies `/api` and `/health` to the api container, so the app and its API share one origin and one published port; the full system's Compose file still runs the development server and is to be moved the same way.

**Amended 2026-11-04 (one compile for the three .NET services, a filtered build context).** The API, the worker and the migrator share every module, yet each had its own Dockerfile and so compiled all of them separately, with the analyzers, the style rules and the OpenAPI document that CI already checks, and a fourth heavy build (the web app) beside them; on the founder's computer the first build of the trader program took over 40 minutes. One Dockerfile, `deploy/docker/dotnet.Dockerfile`, now publishes the three hosts one after another in a single `build` stage (the modules compile once) and has three runtime targets, `api`, `worker` and `migrator`, which both Compose files name with `target:`; Compose builds the shared stage once. The stage switches off what CI gates and the running program does not need (`RunAnalyzers`, `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`, the OpenAPI document written at build) and keeps the NuGet packages in a BuildKit cache mount, as the web image does for the pnpm store, so an update downloads nothing it already has. A `.dockerignore` keeps `.git`, `bin`, `obj`, `node_modules` and test output out of the build context. Measured on a four-core machine: the three services from a cold cache in 107 s, against 160 s for the old API image alone (an estimated 480 s for the three); the published files of the API are identical to the old image's (the same 258 files). The images' entry points, ports and the migrator's embedded migrations are unchanged.

**Amended 2026-11-03 (reached from anywhere).** The founder wants the trader program on his phone wherever he is, and its data is confidential. It is reached through Tailscale, a WireGuard private network between the devices signed in to one account, rather than a port opened on the router or a public tunnel: nothing listens on the internet, each device is authenticated by the account, and Tailscale Serve gives the app an HTTPS address of the machine (`https://<computer>.<tailnet>.ts.net`) with a real certificate, which a home-screen app, passkeys and the camera need. `deploy/windows/share-trader-anywhere.ps1` (run by its `.bat`) turns Serve on for the app's port, writes that address as `QUICKER_TRADER_PUBLIC_ORIGIN` in `.env` (the API's public origin: the passkey relying party and the links in its messages) and restarts the stack. In front of nginx the relay sets `X-Forwarded-Proto` and `X-Forwarded-Host`, which nginx passes on when present, and the API walks the whole forwarded chain (`ForwardLimit` unlimited) past the trusted private hops to the device's address. The free personal plan covers one person's devices; a second person later means inviting them to the tailnet. Not chosen: port forwarding with a dynamic DNS name (exposed, a certificate to manage), a public tunnel behind a login (exposed to the internet by design, a domain to own), hosting elsewhere (the data leaves the founder's machine). When the phone is to carry nothing, the same script with `-Public` (`share-trader-public.bat`) uses Tailscale Funnel instead: the same HTTPS address, opened to the internet through Tailscale's relay, with the program's own sign-in (the password policy, lockouts, the per-address rate limit on `/auth`, two-step verification) as the protection; the founder chose this knowingly (A-204), and the private way stays the recommendation.

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
