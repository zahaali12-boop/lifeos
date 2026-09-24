# ADR-0001: Modular monolith with enforced module boundaries

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

An ERP is one consistent transactional system: a sales invoice touches sales, receivables, inventory, tax and the ledger in one atomic step. A team of one (plus an AI engineer) cannot operate a fleet of services, and "books always right" is far easier to guarantee inside one database transaction than across service boundaries. At the same time, ERPs rot when modules reach into each other's tables; the brief demands strict boundaries.

## Decision

One deployable (`Quicker.Api` and `Quicker.Worker` share the codebase) composed of modules. Each module is a set of .NET projects:

* `<Module>.Contracts`: public DTOs, command/query interfaces, integration event types. The only project other modules may reference.
* `<Module>`: domain model, application services, persistence, HTTP endpoints. Everything `internal`.
* `<Module>.Tests`.

Rules, enforced by ArchUnitNET tests that fail CI:

1. Cross-module references only to `.Contracts`, `Quicker.Kernel` and building blocks.
2. Dependency direction: process modules → core modules → platform modules. Reverse reactions travel as integration events through the outbox (ADR-0010).
3. No module may write journal entries except through `IPostingService` (ADR-0006).
4. Contracts contain no persistence types.
5. No cycles.

Each module owns its tables (a naming prefix per module, for example `sales_`, `inv_`, `gl_`) and no other module's SQL touches them; the reporting schema is fed by projections, not by cross-module joins on live tables.

Module list and responsibilities are in `ARCHITECTURE.md` §6. Extraction of a module into a separate service is a permitted future step only when a measurement (throughput, isolation, team structure) demands it; the Contracts boundary makes that extraction mechanical.

## Alternatives considered

* **Microservices from the start.** Rejected: distributed transactions or sagas across ledger, stock and subledgers would make the core invariants probabilistic, and operations cost is far beyond a one-person team.
* **Unstructured monolith.** Rejected: the brief explicitly forbids it, and it is how legacy ERPs became brittle.
* **Plugin architecture with dynamic loading (Odoo-style).** Rejected for v1: runtime module loading complicates typing, migrations and testing. Extensibility is delivered through configuration (custom fields, workflow, posting profiles, report designer) and the public API/webhooks instead.

## Consequences

* One database transaction can span modules, which is exactly what posting needs.
* Compile-time and test-time enforcement of boundaries; violations cannot merge.
* Slightly more ceremony (Contracts projects, DTO mapping) in exchange for a codebase that a new engineer or session can navigate by module.

## Amendment 2026-09-24 (post-M4 routine pass, as built)

The rules are enforced by `tests/Architecture/Quicker.Architecture.Tests`, part of the solution and so of every CI run. It checks the project files (what a change declares) and the compiled assemblies the API host loads (what the code uses) with plain reflection rather than ArchUnitNET: every rule here is about which assembly may reference which, and reflection answers that without another dependency. As built:

* **Rule 1** holds: a module references only the kernel, the building blocks, its own projects and other modules' `.Contracts`; production code never references test support. The building blocks know no module except `Identity.Contracts`, the caller (principal, grants, scopes), which the request pipeline, the permission filters and the outbox's actor need.
* **Rule 4** holds: contracts reference only the kernel (no EF Core, Npgsql, Dapper or `Quicker.Persistence`).
* **Rule 3** follows from rule 1: no module can reach the posting engine's implementation, only `IPostingService` in `Accounting.Contracts`.
* **Rules 2 and 5** are a ratchet. Two dependencies point against the tiers on purpose (Inventory → Workflow, because stock adjustments are approved through the workflow engine; Numbering → Organization, because series are per company and fiscal period) and two pairs of modules use each other's contracts (Audit ↔ Identity, Inventory ↔ Items); there is no assembly cycle, since contracts reference only the kernel. The test names each with its reason, fails on any new one, and fails when a named one disappears so the list stays true.

