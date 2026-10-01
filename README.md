# Quicker ERP

A production-grade ERP built to beat the incumbents where it matters: books that are always right, depth for the hardest real-world operations, speed and usability, and reporting people love. First markets: Iraq and the Middle East; English and Arabic with full right-to-left support; 80+ currencies; cloud SaaS and on-premise from one codebase.

**Status:** Phase 0 (blueprint) complete and awaiting founder approval. No application code yet. See [docs/PROGRESS.md](docs/PROGRESS.md).

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
