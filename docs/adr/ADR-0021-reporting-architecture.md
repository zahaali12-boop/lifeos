# ADR-0021: Reporting: semantic layer, read models, columnar store when measured

Status: proposed · Date: 2026-09-22

## Context

Reporting is the strongest selling point: a semantic layer for business users, pivots with drill-down to source lines, a financial report designer, dashboards, scheduling and exports, row-level security, and interactive pivots over 10M+ transaction lines in a few seconds without slowing daily work.

## Decision

### Read models in the `reporting` schema (same PostgreSQL cluster)

* Star schema: fact tables `fact_journal_lines`, `fact_sales_lines`, `fact_purchase_lines`, `fact_stock_movements`, `fact_stock_value`, `fact_open_items` (AR/AP, with daily snapshots for aging as of any date computed from settlements rather than stored), `fact_payments`, `fact_budget_lines`, `fact_assets`; dimensions `dim_date`, `dim_company`, `dim_branch`, `dim_account` (with hierarchy paths), `dim_partner`, `dim_item` (with category path), `dim_warehouse`, `dim_sales_rep`, `dim_currency`, `dim_dimension_values`, plus custom-field dimensions generated from definitions flagged reportable.
* Fed by outbox projections (ADR-0010) within seconds of posting; each fact row carries `tenant_id`, `company_id`, `branch_id`, scope columns for RLS, and the **source keys** (journal line id, document line id) for drill-through.
* Partitioned by year, `tenant_id` leading every index, covering indexes for the common cubes, `parallel_workers` tuned; reporting queries run on a **read replica** when available and always under a lower `statement_timeout` and a separate connection pool so they cannot starve transactional work.
* Projections are rebuildable from the append-only ledgers (the same rebuild mechanism as balances) and verified in CI.

### Semantic layer

* **Models** (`rpt_models`): a named cube over one fact with joins to dimensions, exposing **fields** (`rpt_fields`: friendly labels EN/AR, type, format, folder, dimension | measure, aggregation, expression, drill path, security predicate). Core models ship in code; admins add fields (calculated, custom-field based) and models over the same facts in the UI.
* **Query definition** (JSON): model, rows/columns/values (pivot), filters, sort, top-N, calculated fields (safe expression grammar shared with ADR-0020), comparison columns (prior period, YoY, budget), parameters. The **compiler** produces one parameterised SQL statement with the user's row-level predicates injected (company/branch/warehouse scopes, field permissions), limits and timeouts. The compiled SQL is viewable ("show query"), which the AI layer also relies on.
* **Drill-down**: every result cell carries the tuple of dimension keys; the drill request reruns the same filters plus the cell's keys against the fact's line grain, then links to the source document. Two levels: lines, then documents and journal lines.
* **Financial report designer**: row definitions (account ranges, account categories, dimension filters, formulas, headers, totals, sign flipping), column definitions (period, range, budget version, prior year, variance, percentage, currency), rendered through the same compiler over `fact_journal_lines` and `gl_balances` with the fiscal calendar. Layouts for P&L, balance sheet, cash flow (indirect, from account cash-flow categories), trial balance and management reports ship as templates.
* **Dashboards**: widgets are saved queries with chart or KPI rendering, parameters, refresh cadence and drill-down; executive dashboard templates ship (cash position, aging, DSO/DPO, inventory turns and aging, gross margin, budget vs actual).
* **Scheduling and export**: report schedules (cron, recipients, format PDF/Excel/CSV, parameters, condition "only if rows"), executed by the worker; exports stream from the compiler with the same security; Excel export uses real number cells and formats.
* **Security**: every query is compiled with the requesting user's scopes; shared reports never widen access; exports carry a watermark of who exported them (in the audit log and the file's metadata).

### The columnar decision

PostgreSQL first. The M7 benchmark runs the gross-margin pivot (hard scenario 17) and five other reference cubes on a seeded tenant with 10M fact rows; targets: p95 under 5 s cold, under 2 s warm, on a 4-vCPU replica. If PostgreSQL misses after tuning (partition pruning, covering indexes, pre-aggregated `agg_*` tables maintained by the projections for the heaviest cubes), the compiler gains a **ClickHouse** target: the same projections write to ClickHouse via its HTTP interface, models declare their preferred store, and drill-through still goes to PostgreSQL. The compiler's SQL dialect is abstracted from the start so this is an added backend, not a rewrite.

## Alternatives considered

* **ClickHouse or DuckDB from day one.** Strong engines, but a second store is operational cost and a second consistency problem before we know we need it; on-premise installs suffer most. Kept as the measured next step.
* **Embedding a BI tool (Metabase, Superset).** Fast to ship, but breaks the drill-to-document promise, row-level security integration and bilingual UX, and hands the strongest selling point to a third party.
* **Querying transactional tables directly.** Slows daily work and forces reporting joins across modules.

## Consequences

* Business users get a single, secure, explainable query engine used by reports, dashboards, financial statements, exports and AI.
* The projection layer is real work in M7 but is also what makes the transactional schema free to stay normalised.
