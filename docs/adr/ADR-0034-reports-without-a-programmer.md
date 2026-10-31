# ADR-0034: Reports without a programmer: projected facts, a semantic query engine, and the screens on top

Date: 2026-10-31. Status: accepted. Founder programme item 9 (ROADMAP "Founder programme", 7.1–7.5, 7.7, 9.1), ASSUMPTIONS A-182. Refines ADR-0021 (reporting architecture) and ADR-0028 (AI guardrails).

## Context

ADR-0021 fixed the shape: star-schema read models fed from the ledgers, a semantic layer of models and fields, one
query compiler used by the report builder, the financial designer, dashboards, schedules and ask-your-data, every query
compiled with the user's scopes and every cell drillable to its lines. The home dashboard (A-167) and the sales-lines
grid (item 1f) were written as hand-made SQL "until the semantic layer arrives". Item 9 is that layer. What has to be
decided now is how the facts are kept current without a second store, how far the first slice reaches, and how
ask-your-data works without an AI provider key on the machine.

## Decision

1. **Facts in a `reporting` schema, projected by watermark, rebuildable.** `reporting.fact_journal_lines`,
   `fact_sales_lines`, `fact_purchase_lines`, `fact_stock_movements` and `fact_stock_values` carry one row per source line with the keys
   the models join on (company, branch, account, partner, item, warehouse, dimension set, dates, currency) and the
   source document (type, id, line id) for drill-through. Each fact has a projection: an `INSERT … SELECT … ON
   CONFLICT DO NOTHING` from the append-only source after a watermark (the source row's `created_at`, re-scanned with a
   two-minute overlap so a late commit is never missed), run by the worker on a platform schedule every two minutes, kicked by
   `JournalEntryPosted` and `StockPosted` (one job per tenant and minute), run before any query whose sources
   hold rows newer than the fact's watermark (each source's latest stamp is an indexed `max`, so the check costs nothing when nothing changed and a query always sees what was posted before it), and callable on demand (`POST /reporting/projections/refresh`) and as a full rebuild (`/rebuild`, truncating and re-projecting a tenant). Open items
   (AR and AP) and stock on hand are read from the subledgers' live tables through views, since they are state, not
   history. Row-level security applies to the fact tables as to any tenant table.
2. **The semantic catalogue is code first, data second.** System models (`journal_lines`, `sales_lines`,
   `purchase_lines`, `stock_movements`, `open_items`, `stock_on_hand`) and their fields are defined in code with
   bilingual labels, folders, types, aggregations and drill paths, and written into `rpt_models`/`rpt_fields` on first
   use so an admin can relabel them, hide them, and add **calculated fields** (the expression grammar of ADR-0020,
   extended with arithmetic over measures) and **custom-field dimensions** (a custom field of the source document
   becomes a dimension by key). A model names the permission that reads it and the scope column the user's scopes
   apply to.
3. **One query definition, one compiler.** A query is JSON: model, rows, columns, values (field + aggregation),
   filters (field, operator, value or parameter), sort, top-N, calculated fields, comparisons (prior period, prior
   year; budget once item 12 lands), parameters (date range, company). The compiler validates it against the
   catalogue, injects the tenant and the user's company and branch scopes for the model's permission, and emits one
   parameterised `GROUP BY` statement over the fact with the dimension joins it needs; the result is flat (dimension
   keys and labels, measures), pivoted by the client. `explain` returns the SQL with values inlined. `drill` reruns the
   same filters plus a cell's keys at line grain and returns the lines with their document links. Shared reports never
   widen access: the compiler always uses the reader's scopes, never the author's.
4. **Saved reports, shares, dashboards, schedules** are the tables of DOMAIN_MODEL §21 (`rpt_reports`,
   `rpt_report_shares`, `rpt_dashboards`, `rpt_dashboard_widgets`, `rpt_schedules`, `rpt_runs`). Exports stream from
   the compiler: CSV, XLSX with real numbers and dates (written with the OpenXML writer the grid export already uses),
   PDF through the print pipeline (a `report` print model rendering the pivot). A schedule is a tenant entry in
   `ops.schedules` for the job `reporting.run`, which runs the report as the schedule's owner, stores the file as an
   attachment and emails the recipients, skipping the send when "only if rows" finds none.
5. **Financial layouts** (`rpt_financial_layouts`, rows, columns) compile to the same engine over the journal facts:
   rows are account ranges, account categories or formulas; columns are periods, year to date, prior period, prior
   year and variances, in functional or reporting currency. The shipped profit and loss, balance sheet and trial
   balance layouts reproduce the ledger's figures to the cent and drill to accounts and lines.
6. **Ask-your-data produces a query definition, never SQL** (ADR-0028). `POST /reporting/ask` runs the question
   through a provider: the reference adapter calls the tenant's configured model with the catalogue and the examples
   as tools; the built-in **deterministic interpreter** understands the common shapes in English and Arabic ("sales
   by customer this month", "المبيعات حسب العميل هذا الشهر", measures, "by" dimensions, periods, top-N, filters on
   named values) and is what runs when no provider is configured and in tests. Either way the answer shows the
   definition, the SQL and the scope, is editable and saveable as a report, and is logged in `ai_interactions`.

## Delivered in slice 9a (2026-10-31)

The migration `V0068__reporting_facts.sql` (the five facts, the watermarks, the two views, the source indexes), the
projection service, job and event handlers, the catalogue of six system models with the tenant's dimensions, the
compiler with scope predicates, filters, sort, top-N, comparisons and calculated fields, the endpoints `GET
/reporting/models`, `POST /reporting/query`, `/query/explain`, `/query/drill`, `/projections` (`/refresh`, `/rebuild`),
the permissions `reporting.report.run/manage` and `reporting.projection.manage`, and the tests. The saved reports,
the builder, the designer, dashboards, schedules and ask-your-data follow in slices 9b–9e.

## Delivered in slice 9b (2026-10-31)

Saved reports and shares (`V0069__reporting_reports.sql`: `rpt_reports`, `rpt_report_shares`; A-183), the report
service (list what the member owns or is shared with, save after compiling the definition for the author, shares the
owner alone sets, delete by the owner, run as the caller with a company or range override) and its endpoints under
`/reporting/reports`; the **Reports** screen (the saved reports, a new report from any model) and the **report
builder** (`/reports/builder`): the model and its fields by folder, rows, columns and values by a click, aggregation
per value, filters with typed operators, date range, prior-period and prior-year comparison, top-N, sort, calculated
fields, the result as a flat table, a client-side pivot with totals where they add up, or a chart (bars, lines, area;
stacked or horizontal; one series per measure or per dimension) drawn with the chart system's palette and specs, the
drill from any cell to the lines and their documents, the statement behind the report, CSV and XLSX through the table
export, saving with a bilingual name and shares. The freshness rule of slice 9a changed from "facts older than a
minute" to "a source holds rows newer than the fact's watermark" (decision 1 above), because a query a few seconds
after a posting must see it. The designer, dashboards, schedules (and PDF export through the print pipeline) and
ask-your-data follow in slices 9c–9e.

## Consequences

* Reports, statements, dashboards, exports and the AI share one secure engine; adding a model is a code change with
  tests, adding a field or a report is an admin's work.
* Projection lag is seconds, visible on the report (projected up to …), and a stale fact can always be refreshed or
  rebuilt; nothing financial depends on the facts.
* The columnar store of ADR-0021 stays a measured option: the compiler targets SQL through one interface.
* Deferred: budget columns (item 12), the print template designer (7.6), the benchmark on 10M rows (7.8), anomaly
  detection, forecasting and invoice capture (9.2–9.4).
