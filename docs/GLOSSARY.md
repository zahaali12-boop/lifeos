# Glossary

Terms used consistently across the documentation, code and UI. Arabic UI terms are chosen in M1 and recorded here alongside.

| Term | Meaning in Quicker |
|------|--------------------|
| Tenant | A customer organisation using Quicker; the unit of data isolation. Contains companies. |
| Company | A legal entity with its own chart of accounts (or shared), functional currency, fiscal calendar and books. |
| Branch | An operating location within a company; also a system dimension on every journal line. |
| Warehouse | A stock location belonging to a company (and optionally a branch); may have bins. |
| Functional currency | The currency a company keeps its books in (`amount_fc`). |
| Transaction currency | The currency a document is issued in (`amount_tc`). |
| Reporting currency | An additional currency every line is also stored in (`amount_rc`), used for group reporting. |
| Posting date | The accounting date of a document; selects the fiscal period and exchange rate. |
| Document date | The date printed on a document (invoice date, tax point). |
| Fiscal period | A slice of a fiscal year with an open/soft-closed/hard-closed state per module. |
| Posting engine | The single service that turns a `PostingRequest` into a balanced journal entry. |
| Account role | A symbolic account name (`Revenue`, `AR`, `Inventory`) that the posting profile resolves to a real account. |
| Posting profile / posting rule | Admin configuration mapping (role, keys) to accounts, most specific match wins. |
| Posting group | An attribute of items, partners, banks, assets or charges used as a key in posting rules. |
| Control account | A GL account whose balance equals a subledger (AR, AP, inventory, bank, PDC, GRNI, FA, IC). |
| Subledger | The detailed records behind a control account: open items, stock value entries, bank transactions, asset transactions, cheques. |
| Open item | An unsettled AR or AP document (invoice, credit note, deposit, on-account payment, cheque). |
| Settlement | A row recording that one open item settled part of another, with FX, discount, WHT, charge and write-off amounts. |
| SLE (stock ledger entry) | One quantity movement of an item in a warehouse, lot and serial. |
| SVE (stock value entry) | One value amount attached to an SLE: initial cost, invoice correction, landed cost or cost adjustment. |
| Application | The link between an outbound SLE and the inbound SLE(s) whose cost it consumed (FIFO). |
| Expected cost | The value of a receipt before its supplier invoice is posted (PO price); replaced by actual cost on invoice. |
| Cost adjustment run | The routine that re-applies costs from a date forward after a backdated or late event and posts the differences. |
| Landed cost | Freight, customs, duty, insurance and similar charges allocated onto receipts, and onward to COGS for sold quantities. |
| GRNI | Goods received not invoiced: the accrual for receipts awaiting a supplier invoice. |
| Three-way match | Comparison of PO, receipt and supplier invoice within price and quantity tolerances. |
| Block / override | A structured refusal (credit limit, match variance, negative stock, price floor, budget, period) and the logged approval that lets it through once. |
| Workflow definition | Admin-configured approval rules (conditions, steps, approvers) for an entity or a block kind. |
| Dimension | An analytic tag on journal lines (branch, cost centre, department, project, custom); a dimension set is one combination. |
| Gapless series | A numbering series that can never skip a number; allocated at posting time. |
| Draft identifier | The temporary identifier a document carries before posting. |
| Reversal | A mirror journal (sides swapped) linked to the original; the only way to undo a posted document. |
| Correction | A new document that replaces a reversed one, linked with `corrects`. |
| Soft close / hard close | Period states: finance-only posting vs no posting. |
| Year-end close | The closing entry that moves P&L balances to retained earnings. |
| FX revaluation | Month-end restatement of open foreign-currency items at the closing rate; auto-reversed next period for open items. |
| Realized FX | The gain or loss recognised when a foreign-currency item is settled at a rate different from its booked rate. |
| PDC | Post-dated cheque, received or issued; tracked in its own control accounts through its life cycle. |
| ATP | Available to promise: on hand minus reserved plus expected receipts within the horizon. |
| FEFO | First expired, first out picking suggestion for lot-tracked items. |
| Semantic layer | The catalogue of models and friendly fields that reports, dashboards and the AI query through. |
| Fact table / dimension table | Read-model tables in the `reporting` schema fed by projections. |
| Projection | An outbox consumer that maintains a read model (reporting fact, search index, statistic). |
| Outbox | The table where integration events are committed with the business data and later dispatched. |
| Job | A unit of background work in the `ops.jobs` table. |
| RLS | PostgreSQL row-level security, the database-level tenant boundary. |
| Invariant harness | The automated checks that the books are right (balanced, subledgers equal controls, valuation equals GL, derived equals rebuilt). |
| Hard scenario | One of the 18 end-to-end situations in the brief that must be proven by an automated test. |
