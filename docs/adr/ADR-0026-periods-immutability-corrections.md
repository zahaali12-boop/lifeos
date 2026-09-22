# ADR-0026: Periods, locks, immutability and the correction policy

Status: proposed · Date: 2026-09-22

## Context

Posted records are immutable; posting into a closed period is blocked and corrections post into the open period referencing the original (hard scenario 7); soft and hard locks; close checklist; year-end close to retained earnings with controlled, audited reopening (scenario 16).

## Decision

### Period states, per company, per module

`fiscal_periods` carry a state per module (`GL`, `AR`, `AP`, `INV`, `FA`, `BANK`, `TAX`):

| State | Who can post | Notes |
|-------|--------------|-------|
| `open` | anyone with posting permission | default for current and future periods within the fiscal year |
| `soft_closed` | roles with `periods.post_in_soft_closed` (typically finance) | used during month-end while operations move on |
| `hard_closed` | nobody | reopening requires `periods.reopen`, a reason, optional step-up auth, and is audited; reopening is per module and per period |
| `never_opened` | nobody | future years not yet opened |

Additional company-level "allow posting from/to" dates restrict backdating for specific roles.

### Blocking and correcting

* The posting engine resolves the fiscal period for `(company, posting_date, module)` and refuses if the state forbids the actor; the error carries the period, its state and who can reopen it.
* **Correction into the open period**: the reversal service posts the mirror entry with `posting_date` = the later of the original date and the first open period start, links `reverses_entry_id`, and the corrected document (if any) references the original (`corrects_document_id`). Both directions are navigable and show on both documents' timelines.
* Value-entry adjustments from costing follow the same rule (ADR-0008).

### Immutability of documents

* After posting, a document's financial fields are frozen at the database level (a trigger allows updates only to a listed set of non-financial columns such as notes, attachments count, print count, external references, workflow state fields). Changing anything financial means reverse and re-create (the UI offers "Correct" which does both and copies the draft).
* Master data that appears on posted documents is snapshotted on the document (names, addresses, tax ids, terms at posting time) so later edits to master data do not change history.

### Close checklist and year-end

* `close_checklists` per company: template tasks (bank reconciliations complete, shipped-not-invoiced accrued, receipts-not-invoiced reviewed, FX revaluation run, depreciation run, tax return prepared, subledger equals control checks passed, inventory count variances approved), each with owner, status, evidence link, and automatic checks where possible (the invariant queries run as checklist items).
* **Year-end close** posts a closing journal per company (and optionally per dimension set) moving P&L balances to retained earnings into a special closing period (`period 13` or the last day flagged `is_closing_entry`), marks the year `closed`, and opens the next year's opening balances. Reopening a closed year reverses the closing entry (linked), requires `year.reopen` permission and reason, and is audited; re-closing posts a new closing entry.
* Comparative reports exclude closing entries by default and can include them.

## Alternatives considered

* **Editable posted documents with audit trail.** Common in small-business tools; violates immutability and makes tax audits painful.
* **Single period lock for all modules.** Prevents the standard practice of closing inventory before GL.

## Consequences

* Period control is precise and auditable; corrections leave a clear two-way trail.
* Frozen documents plus snapshots make historical prints and reports stable forever.
