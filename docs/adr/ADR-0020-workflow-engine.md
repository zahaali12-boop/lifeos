# ADR-0020: Workflow and approval engine

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Multi-level approval rules with thresholds by amount, department, category, supplier or customer; delegation, escalation, mobile approvals; credit holds and three-way-match blocks routed through approval with every override logged; configurable in the UI.

## Decision

### Model

* `wf_definitions`: tenant, entity type (any document or master record), trigger (`on_submit`, `on_post_attempt`, `on_field_change`, `on_block(kind)`), status (draft | active | retired), version; the active version at trigger time is stamped on each request.
* `wf_rules` (ordered within a definition): condition, then steps. Conditions are written in a **safe expression language** (a small, sandboxed grammar: field access, comparisons, arithmetic, `in`, `and/or/not`, functions like `amount_in('USD')`, `days_overdue()`, `is_new_supplier()`), parsed and type-checked against the entity's field catalogue including custom fields; no code execution. The UI offers a rule builder that produces the expression and shows it.
* `wf_steps`: approver resolution (specific users, roles with scope match, the requester's manager chain from `org_positions`, dynamic: cost-centre owner, purchase category owner, customer's account manager), `mode` (any one | all | quorum n), timeout and escalation target, `allow_delegate`, `require_comment`, `require_step_up_auth`.
* `wf_requests`: per document instance: current step, status (pending, approved, rejected, cancelled, expired), history; `wf_actions`: approve, reject, request changes, delegate, escalate, comment, with actor, timestamp, channel (web, mobile, email link), and IP. Every action is also an audit event (ADR-0015).
* `wf_delegations`: user → delegate, scope (all or definition), period; delegations are visible in the request history.
* Notifications go out through Collaboration; email approval links carry single-use signed tokens and still require login unless the tenant allows "one-click approve" for low-risk definitions.

### Integration with documents

* Document state machine reserves `pending_approval`; submit runs the definitions for the entity; if no rule matches, the document is auto-approved (recorded as such).
* **Blocks** (credit limit exceeded, three-way-match variance, negative stock, price below floor, budget exceeded) are raised by the owning module as a structured `Block` (kind, rule, values); the workflow engine routes a `Block` to its definition and, on approval, records an **override** (`wf_overrides`: block, approver, reason, expires) that the module honours once. Overrides are exactly what the brief calls "every override logged".
* Amount thresholds evaluate in the company's functional currency at the document's rate unless the rule specifies a currency.
* Re-approval policy per definition: any change after approval resets to draft (default) or only changes to listed fields.

### Operations

* An "Approvals" inbox (desktop and mobile web) with bulk approve, filters, and full document preview including the "why" panel (why it needs approval, which rule, which values).
* SLA and escalation timers are jobs (ADR-0010); overdue approvals escalate and appear on dashboards.
* Definitions and rules are versioned and exportable as part of configuration templates.

## Alternatives considered

* **BPMN engine (Camunda, Elsa).** Powerful, but overkill for approval flows and hard to keep tenant-safe and auditable; the ERP needs are approval chains and blocks, not arbitrary process graphs.
* **Hard-coded approval per document type.** How many ERPs start; the brief requires admin configuration.
* **Full scripting language (JavaScript rules).** Flexibility at the cost of security and auditability; the sandboxed expression grammar covers the real cases and is analysable.

## Consequences

* One engine serves purchasing approvals, credit releases, journal approvals, master data changes and price exceptions.
* Overrides are first-class records, so audits can list every exception with who approved it and why.
