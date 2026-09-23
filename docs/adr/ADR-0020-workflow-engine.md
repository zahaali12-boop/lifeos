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

## Amendment 2026-09-23 (slice 4.0, as built)

* **Provider contract.** A module registers an `IWorkflowSubjectProvider` per entity type: the fields a rule may use (name, type, bilingual label), the block kinds it raises, `LoadAsync(entityId)` for the inbox and `OnDecidedAsync(decision)` returning a `Result`. The decision runs inside the approver's unit of work; a failure fails the decision and rolls the request back, so an approval is never recorded against a document that did not move (ASSUMPTIONS A-106).
* **Triggers.** `on_submit`, `on_post_attempt` and `on_block` (with block kind) are the triggers; `on_field_change` is deferred to the re-approval policy, which is stored (`reset` | `none`) and enforced when documents re-evaluate after a change.
* **Approver resolution.** Named members or a role (every active holder, scoped to the document's company by default). The manager chain and dynamic owners wait for `org_positions` and the masters they reference. The requester is excluded from every step; a step left without approvers refuses the submission.
* **Grammar.** As designed, plus `=`/`<>` as spellings of `==`/`!=` and the tuple form `in ('a', 'b')`. Block rules reason over the block's `why` values, typed when the block is raised; the definition editor accepts unlisted names for the `on_block` trigger. `amount_in('CCY')` converts from the subject's `currency` through the spot rate of the day.
* **Overrides.** Single-use, expiring after the definition's `override_valid_hours` (default 168); the block is `pending` while its request runs, `overridden` when granted, `cleared` when consumed, and stays `open` when no definition routes its kind.
* **Escalation.** The hourly `workflow.escalate` job adds the escalation target's members to the overdue step, restarts its SLA and notifies; a step without a target reminds its approvers once.
* **Email approval links, one-click approve, bulk approve and step-up authentication** are stored as flags (`require_step_up`) or deferred to 8.1; nothing pretends to enforce them.
