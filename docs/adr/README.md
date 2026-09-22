# Architecture Decision Records

One record per decision that is expensive to reverse. Format: Context, Decision, Alternatives considered, Consequences. Status is `proposed` until the founder approves the blueprint, then `accepted`; superseded records stay in place with a pointer to their replacement.

| ID | Title | Status |
|----|-------|--------|
| [ADR-0001](ADR-0001-modular-monolith.md) | Modular monolith with enforced module boundaries | proposed |
| [ADR-0002](ADR-0002-backend-stack.md) | Backend stack: C# on .NET 10 | proposed |
| [ADR-0003](ADR-0003-postgresql-and-data-access.md) | PostgreSQL as system of record; data access and migrations | proposed |
| [ADR-0004](ADR-0004-multi-tenancy.md) | Multi-tenancy: shared schema with row-level security, dedicated-database tier, on-premise | proposed |
| [ADR-0005](ADR-0005-money-quantities-rounding.md) | Money, quantities, decimals and rounding | proposed |
| [ADR-0006](ADR-0006-posting-engine.md) | Single posting engine and account determination | proposed |
| [ADR-0007](ADR-0007-append-only-ledgers.md) | Append-only ledgers and derived balances | proposed |
| [ADR-0008](ADR-0008-inventory-costing.md) | Inventory costing: quantity and value entries, FIFO applications, backdating | proposed |
| [ADR-0009](ADR-0009-concurrency-and-idempotency.md) | Concurrency, locking and idempotency | proposed |
| [ADR-0010](ADR-0010-events-outbox-workers.md) | Domain events, transactional outbox and background workers | proposed |
| [ADR-0011](ADR-0011-identifiers-time-dates.md) | Identifiers, timestamps, dates and time zones | proposed |
| [ADR-0012](ADR-0012-api-design.md) | API design: REST, OpenAPI 3.1, versioning, pagination, webhooks | proposed |
| [ADR-0013](ADR-0013-frontend-stack.md) | Front-end stack, design system, grids, i18n and RTL | proposed |
| [ADR-0014](ADR-0014-authn-authz.md) | Authentication and authorization model | proposed |
| [ADR-0015](ADR-0015-audit-log.md) | Tamper-evident audit log | proposed |
| [ADR-0016](ADR-0016-numbering-series.md) | Numbering series: gapless and non-gapless | proposed |
| [ADR-0017](ADR-0017-multi-currency.md) | Multi-currency: three amounts per line, rate types, realized and unrealized FX | proposed |
| [ADR-0018](ADR-0018-tax-engine.md) | Tax engine and e-invoicing adapters | proposed |
| [ADR-0019](ADR-0019-custom-fields-extensibility.md) | Custom fields, attachments, comments and saved views | proposed |
| [ADR-0020](ADR-0020-workflow-engine.md) | Workflow and approval engine | proposed |
| [ADR-0021](ADR-0021-reporting-architecture.md) | Reporting: semantic layer, read models, columnar store when measured | proposed |
| [ADR-0022](ADR-0022-print-and-pdf.md) | Print templates and PDF rendering | proposed |
| [ADR-0023](ADR-0023-search.md) | Global search | proposed |
| [ADR-0024](ADR-0024-deployment-ci.md) | Deployment, environments, CI/CD and one-command setup | proposed |
| [ADR-0025](ADR-0025-security-observability.md) | Security and observability baseline | proposed |
| [ADR-0026](ADR-0026-periods-immutability-corrections.md) | Periods, locks, immutability and the correction policy | proposed |
| [ADR-0027](ADR-0027-localization.md) | Localization and the bilingual data model | proposed |
| [ADR-0028](ADR-0028-ai-guardrails.md) | AI layer design and guardrails | proposed |
| [ADR-0029](ADR-0029-testing-strategy.md) | Testing strategy and accounting invariants | proposed |
| [ADR-0030](ADR-0030-pricing-engine.md) | Pricing engine determinism and explanation | proposed |
