# ADR-0029: Testing strategy and accounting invariants

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Every milestone ships with unit, integration and end-to-end tests including its hard scenarios; accounting invariants are verified automatically; tests check real behaviour and never special-case code.

## Decision

### Test pyramid

| Layer | Tooling | What it proves |
|-------|---------|----------------|
| Unit | xUnit, FsCheck property tests | Kernel types (Money, Quantity, rounding, allocation, UoM), pricing pipeline, tax computation, costing re-application on in-memory sequences, expression grammar |
| Architecture | ArchUnitNET | Module boundaries, no floating point, no direct GL writes, Contracts purity |
| Integration | xUnit + Testcontainers (real PostgreSQL), per-module | Repositories, RLS, posting engine, numbering, locks, outbox, projections, migrations |
| Scenario (API-level e2e) | xUnit over the in-process API with a real database; one test class per hard scenario | The 18 hard scenarios and every module's acceptance criteria, exercised through the public API only |
| Browser e2e | Playwright (EN and AR/RTL) | Critical user journeys, accessibility (axe), keyboard navigation, print preview |
| Contract | oasdiff, generated client compile | API compatibility |
| Load | k6 against staging with the 10M-line tenant | Latency budgets (ASSUMPTIONS A-043) |
| Chaos-lite | Integration tests that kill the worker mid-job and restart | Idempotency, outbox recovery, job reclaim |

### Invariant harness (`tests/Invariants`)

A reusable checker invoked at the end of every scenario test and nightly in production (read-only):

1. **Every journal entry balances** in tc, fc and rc (also a DB constraint trigger).
2. **Trial balance sums to zero** per company, period and currency, as of any date sampled (each scenario samples at least three dates including a backdated one).
3. **Subledger equals control**: AR open items = AR control balance; AP = AP control; GRNI accrual = uninvoiced receipts; PDC receivable/payable = outstanding cheques; bank subledger = bank GL; fixed asset net book value = FA cost − accumulated depreciation; intercompany due to/from mirror between companies. All per company, currency and date.
4. **Inventory valuation equals inventory GL** per company and posting group, as of any date, to the minor unit; on-hand quantities equal the sum of stock ledger entries; reserved never exceeds on hand unless negative stock is enabled; FIFO remaining quantities equal on hand.
5. **Derived tables equal rebuilt tables** (`gl_balances`, `inv_stock_balances`, `inv_item_costs`, open items, reporting facts).
6. **Gapless series have no gaps**; posted documents have exactly one journal entry (or one reversal pair).
7. **Audit chain verifies**; every posted document and override has audit events.
8. **Tenant isolation**: the checker runs under a second tenant and sees nothing.

A scenario test fails if any invariant fails, so a scenario cannot pass by breaking the books elsewhere.

*Implementation note (M2.6):* items 1, 2, 5 (`gl_balances`), 6 (numbering), 7 and 8 run as the `Quicker.Integrity` harness (`IInvariantHarness`, `POST /platform/integrity/run`, job `integrity.check_all`); item 8 is checked from the catalogue (every tenant table policed and forced) since the tenant context cannot see other tenants; items 3 and 4 join with the subledgers and inventory. Accounting scenario tests end with the harness (ASSUMPTIONS A-094).

### Rules of engagement

* Tests use the public API or module Contracts, never internals, except unit tests of pure functions.
* No mocks of the database or the posting engine in integration and scenario tests; Testcontainers gives a real PostgreSQL per test run, with a template database to keep runs fast.
* Deterministic seeds: the demo seeder and all generators take a fixed seed; time is controlled through `IClock`.
* Coverage is measured but not gamed; mutation testing (Stryker.NET) runs weekly on Kernel, Posting and Costing.
* Flaky tests are bugs: quarantining is not allowed; the fix is in the same PR or the test is deleted with a written reason.

### Demo data

A deterministic seeder builds the demo tenant: three companies (IQD, USD, AED functional), five branches, eight warehouses, 5,000 items with variants, lots and serials, 800 customers, 300 suppliers, a full year of documents (about 250k lines) with returns, backdated receipts, late landed costs, FX settlements, cheques and counts, so both demos and tests run on realistic data. A larger 10M-line variant exists for M7/M10 benchmarks.

*Implementation note (M1.12, M2.7):* the seeder grows with the milestones. v1 built the organisation layer (companies, branches, users, roles, a year of rates); v2 adds a chart, a posting profile, dimensions and a year of posted journals per company through the accounting services, with the routines run, periods closed, one correction and the invariant harness green before the seed commits (ASSUMPTIONS A-087, A-095). Items, warehouses, partners and documents arrive with M3–M6.

## Alternatives considered

* **Mock-heavy unit tests for services.** Fast, but they prove the mocks, not the books.
* **Manual QA for scenarios.** Not repeatable; the brief requires automation.

## Consequences

* CI time is significant (target under 20 minutes with sharding); worth it.
* The invariant harness is the product's conscience: it runs everywhere, including production.
