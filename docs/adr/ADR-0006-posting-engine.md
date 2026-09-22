# ADR-0006: Single posting engine and account determination

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Every financial event must create a balanced journal through one engine, with accounts chosen by configurable rules rather than hard-coded in modules. Subledgers must reconcile to control accounts at every moment.

## Decision

### The contract

`Accounting.Contracts.IPostingService.Post(PostingRequest) → PostingResult` is the only way to create a journal entry. A `PostingRequest` contains:

* header: company, branch, source document (type, id, number), posting date, document date, description, currency, exchange rate override (with reason) if any, `is_reversal_of`, idempotency key;
* lines: `account_role` (enum such as `ReceivablesControl`, `Revenue`, `OutputTax`, `InventoryAsset`, `Cogs`, `GoodsReceivedNotInvoiced`, `BankAccount`, ...), determination keys (item posting group, partner posting group, tax code, warehouse, branch, bank account, fixed asset category, ...), `amount` in transaction currency (signed: positive debit, negative credit), dimensions, tax details, `subledger_ref` (open item id, stock value entry id, asset id, bank transaction id), optional `explicit_account_id` (only allowed for manual journals and for roles flagged "user-selectable");
* the caller's list of **expected subledger effects** (open items created or settled, value entries), so the engine can cross-check.

### The engine

1. **Account determination** through the company's posting profile: for each line, resolve `(role, keys)` → account using most-specific-match ordering (all keys → fewer keys → default). The profile row used is recorded as `posting_rule_id` on the journal line. Unresolvable roles fail the posting with a structured error listing the missing rule; there is no silent default account.
2. **Currency**: convert each line to functional and reporting currency with the rate for the posting date and rate type (ADR-0017); write `amount_tc`, `amount_fc`, `amount_rc` and the rates. Add a rounding line if functional or reporting totals are off by rounding.
3. **Validation**: debits equal credits in all three currencies; posting date in an open period for the source module and the user's rights (ADR-0026); every control-account line has a subledger reference whose amount equals the line amount; dimensions required by the account are present and valid; account allows this source (manual posting to control accounts is blocked); document is not already posted (idempotency).
4. **Persist** journal entry and lines (append-only, ADR-0007), increment `gl_balances`, and return the entry id.
5. **Reversal**: `Reverse(journalEntryId, reversalDate, reason)` creates a mirrored entry with sides swapped, links both ways, and the subledger open items or value entries are reversed by their owning modules in the same transaction (the engine validates that the subledger effects mirror too).

### Posting profiles (admin-configurable)

Tables: `gl_posting_profiles` (per company, versioned, effective dates), `gl_posting_rules` (profile, account role, key columns nullable: document type, item posting group, partner posting group, tax code, warehouse, branch, bank account, asset category, charge type → account). Item posting groups and partner posting groups are attributes of items/categories and partners. Every template chart ships with a complete default profile so a new company can post on day one; the setup wizard shows unresolved roles before go-live.

### Reconciliation guarantee

Control accounts are flagged `is_control` with `subledger_type` (`AR`, `AP`, `INV`, `FA`, `BANK`, `PDC`, `GRNI`, `IC`). Journal lines on control accounts must carry a matching `subledger_ref` (database check), and a subledger open item must reference the journal line that created it. The invariant test sums control account balances and subledger open balances by company, currency and date and asserts equality (hard scenario 15).

## Alternatives considered

* **Modules choose accounts and call a thin GL API.** How several ERPs started; account logic scatters and configuration becomes code. Rejected.
* **Posting through events (async).** Rejected: the ledger would lag the document, breaking "real time" and making failures invisible to the user who posted.
* **Rules engine with scripting.** Powerful but unbounded; most-specific-match tables cover what SAP, Dynamics and NetSuite offer and stay auditable. A safe expression language is reserved for the workflow engine (ADR-0020).

## Consequences

* Modules are simpler: they compute amounts, not accounts.
* Configuration is data with versions and audit trail; changing a rule never rewrites history because lines store the rule used.
* One place to test balance, period, currency and subledger rules.
