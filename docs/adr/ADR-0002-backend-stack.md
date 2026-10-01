# ADR-0002: Backend stack: C# on .NET 10

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

The brief requires strong typing, first-class decimal arithmetic, mature migrations, excellent testing tools and a large hiring pool, and asks for boring, proven technology in the core. The system must hit p95 < 300 ms on everyday calls and post documents (including cost adjustment chains) in real time. The founder is the only person on the team; most code is written by an AI engineer across sessions, so consistency, compile-time safety and refactorability matter more than terseness.

## Decision

**C# 14 on .NET 10 LTS** with ASP.NET Core.

* `System.Decimal` is a native 128-bit decimal type: money and quantities are exact without a library, and mixing `double` into money is caught by the type system and an analyzer rule that forbids `double`/`float` in domain projects.
* ASP.NET Core minimal APIs with source-generated OpenAPI 3.1; NSwag generates the TypeScript client so the UI is a first-class API consumer.
* EF Core 10 (Npgsql) for aggregate persistence and change tracking; Dapper and hand-written SQL for ledgers, balances and reporting (ADR-0003).
* Hosted services for workers; no external scheduler.
* Analyzers: nullable reference types on, warnings as errors, `Meziantou.Analyzer`, `SonarAnalyzer.CSharp`, custom analyzer forbidding floating point in `Quicker.Kernel`-dependent projects and forbidding `DateTime.Now`.
* Kernel types: `Money`, `Quantity`, `ExchangeRate`, `RoundingPolicy`, `TenantId`, `CompanyId`, strongly typed ids (source-generated), `Result<T>` with domain errors.

Version policy: LTS releases only; upgrade within six months of each LTS.

## Alternatives considered

| Option | Why not |
|--------|---------|
| **TypeScript end-to-end (Node.js)** | Largest hiring pool and one language for UI and API, but no native decimal type (a library plus discipline), weaker nominal typing for money, and single-threaded compute for cost re-application chains. The API-first rule already forces the UI through the generated client, so sharing a language buys less than it seems. |
| **Kotlin/Java on the JVM** | `BigDecimal` is first-class and the ecosystem is proven in ERP (NetSuite, Zoho). Rejected on ergonomics and footprint: verbose decimal arithmetic (`a.multiply(b).setScale(...)`), heavier memory, slower startup for on-premise installs; hiring pool for Kotlin specifically is smaller than for C#. Close second. |
| **Python (Django)** | `Decimal` in the standard library and Odoo/ERPNext prove the domain fit, but weak typing at scale and throughput per core make the latency target and a million-line codebase harder. |
| **Go** | Strong typing and performance, but no native decimal, thin ORM/migration ecosystem for a large relational model, and slower to express a rich domain model. |
| **Rust** | Exact decimals via crate, best performance, but the smallest hiring pool and slowest iteration for a solo team. |

## Consequences

* Two languages in the repository (C# and TypeScript). Accepted because the API contract is the seam and the client is generated.
* .NET tooling gives us `dotnet format`, analyzers, Testcontainers, Playwright and ArchUnitNET out of the box.
* Hiring: C# is among the most common languages in the Middle East's enterprise software market (Dynamics, Sage, Xero-style products), which fits the target region.
