# Quicker ERP

A production-grade ERP built to beat the incumbents where it matters: books that are always right, depth for the hardest real-world operations, speed and usability, and reporting people love. First markets: Iraq and the Middle East; English and Arabic with full right-to-left support; 80+ currencies; cloud SaaS and on-premise from one codebase.

**Status:** in build; what is done, what is next and the known gaps are in [docs/PROGRESS.md](docs/PROGRESS.md).

## Run it

With Docker Desktop running, from the repository folder (Command Prompt, PowerShell or a shell; `make up` does the
same on macOS and Linux):

```
docker compose up -d postgres minio mailpit
docker compose run --rm migrator            # migrations, reference data, the demo tenant when it is missing
docker compose up -d api worker web
```

Then open http://localhost:5173 and sign in as `owner@quicker.example` with `DemoPass2026!`. The API answers on
http://localhost:8080 (`/health/ready` says whether it is up); when another program already uses 8080, copy
`.env.example` to `.env`, set `QUICKER_API_PORT` to a free port and start again. Useful afterwards:

```
docker compose run --rm migrator demo       # rebuild the demo tenant from scratch (about ten minutes)
docker compose logs --tail=100 api          # why something did not work
docker compose down                         # stop everything (keeps the data); `down -v` also deletes the database
```

**Automatic updates on Windows.** Double-click `deploy\windows\setup-auto-update.bat` once (in a folder from a ZIP
download or a clone). It installs Git when it is missing, turns the folder into a checkout of the working branch (a
GitHub sign-in window opens the first time), registers the hidden scheduled task "Quicker auto-update", and updates
once. From then on the task checks GitHub at sign-in and every 30 minutes; when something new arrived it takes it and
rebuilds, the migrator upgrading the database before the API starts, while the running version stays up until the
new one is built. Each update is written to `update.log`; `deploy\windows\update.bat` updates at once;
`remove-auto-update.bat` turns it off. Your `.env` and your data are kept. Docker Desktop should start when you sign in
(its default). Every long-running part has the restart policy `unless-stopped`, so Docker starts it again after
Windows restarts and after a crash (say, memory running out while an update builds); the migrator stays a one-shot.
If the app ever says its server is not answering for more than a few minutes, open Docker Desktop, Containers, and
press Start on the `lifeos` row.

To work on the web app with hot reload outside Docker: `pnpm install`, then `set VITE_API_BASE_URL=http://localhost:8080`
(`export` on macOS and Linux) and `pnpm --filter @quicker/web dev`.

## Start here

1. [docs/PRODUCT_BRIEF.md](docs/PRODUCT_BRIEF.md): what we are building and why.
2. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/adr/](docs/adr/README.md): how, and why each decision was made.
3. [docs/DOMAIN_MODEL.md](docs/DOMAIN_MODEL.md) and [docs/POSTING_RULES.md](docs/POSTING_RULES.md): the data and the accounting behaviour.
4. [docs/ROADMAP.md](docs/ROADMAP.md): the order of work, milestone by milestone.
5. [docs/ASSUMPTIONS.md](docs/ASSUMPTIONS.md): open questions and defaults.

## Stack (decided in Phase 0)

C# on .NET 10 LTS, PostgreSQL 17, React 19 + TypeScript. Modular monolith, one posting engine, append-only ledgers, shared-schema multi-tenancy with row-level security. Details and alternatives in the ADRs.

## Repository note

The files `index.html`, `capacitor.config.json`, `package.json` and `build-apk.yml` at the repository root belong to a pre-existing, unrelated "Life OS" mobile app. They are untouched in Phase 0; their relocation is question Q2 in `docs/ASSUMPTIONS.md`.
