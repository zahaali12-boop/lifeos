# ADR-0028: AI layer design and guardrails

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Ask-your-data, anomaly detection, forecasting, reorder suggestions, supplier-invoice capture, all respecting permissions, showing the query behind every answer, and never posting without human approval. Built in M9 but designed for from the start.

## Decision

### Boundaries that hold for every AI feature

1. **AI acts as the user.** Every AI call runs in the user's session with the user's effective permissions and scopes; the AI has no service account with wider access.
2. **AI never executes SQL.** Ask-your-data produces a **semantic-layer query definition** (ADR-0021), which is validated, compiled and executed by the same engine as the report builder; the user sees the definition and the compiled SQL and can edit and save it as a report.
3. **AI never posts.** The only write operations available to AI tools are: create drafts (supplier invoice draft from capture, purchase suggestion draft, journal draft for an anomaly correction), add comments, and propose (not apply) master-data changes. Every AI-created draft is labelled with its origin and confidence and enters the normal approval and posting path.
4. **Explainability.** Every answer carries: the query or rule used, the data scope, the model and prompt version, and a confidence or coverage note; anomaly findings carry the rule (deterministic) or the features (statistical) that triggered them.
5. **Privacy.** Tenant data sent to a provider is limited to the fields needed; providers are configured per tenant (Claude API reference implementation; on-premise tenants may choose a local model or disable AI); prompts and responses are logged in `ai_interactions` for audit with retention settings.
6. **Feedback loop.** Users rate answers and corrections are stored to improve prompts and few-shot examples per tenant; no training on tenant data.

### Features (M9)

* **Ask-your-data**: natural language → semantic query, with tool access to the field catalogue, saved reports, fiscal calendar and the user's defaults; renders as a report the user can inspect, edit and save; supports Arabic and English.
* **Anomaly detection** as deterministic rules plus statistical scoring: duplicate supplier invoices (same supplier, amount, close dates, similar reference, fuzzy match), unusual margins per item/customer versus history, journal entries with unusual accounts, amounts, times or users, round-number and just-under-threshold patterns, dormant supplier reactivation, price changes outside bands. Findings appear in a review queue with one-click drill-down.
* **Demand forecasting and reorder suggestions**: classical time-series baselines (seasonal naive, exponential smoothing, Croston for intermittent demand) run by the worker; the LLM explains and adjusts suggestions but never bypasses the reorder policy; outputs are purchase suggestion drafts.
* **Supplier-invoice capture**: PDF or photo → vision model extraction → match to supplier, PO and receipts → draft supplier invoice with line-level confidence and highlighted fields; the user confirms.

### Data model prepared now

`ai_interactions` (tenant, user, feature, input, tool calls, output, model, prompt version, tokens, latency, rating), `ai_findings` (anomaly queue), `ai_capture_jobs`, `ai_settings` per tenant (provider, model, enabled features, data-sharing consent). The semantic layer and draft-only APIs are the integration points, so M9 adds features without touching the ledger.

## Alternatives considered

* **LLM writes SQL directly.** Faster to build; impossible to secure at row and field level without re-implementing the semantic layer.
* **AI with posting rights under confidence thresholds.** Tempting for invoice automation; rejected by the brief and by audit expectations. Human approval remains mandatory.

## Consequences

* The AI layer is an additional consumer of the same secure interfaces, which keeps the guarantees intact.
* Investment in the semantic layer in M7 pays twice.
