# Quicker ERP: Posting Rules

The matrix of every business document and the journal entries it creates, including reversals, FX and landed-cost adjustments. This is the specification the posting engine (ADR-0006) implements and the scenario tests verify. Accounts are named by **account role**; the company's posting profile maps roles (with keys such as item posting group, partner posting group, tax code, warehouse) to real accounts.

Conventions:

* `Dr` / `Cr` in transaction currency; the engine converts to functional and reporting currency per line (ADR-0017) and adds a `Rounding` line if needed.
* *Control* roles carry a subledger reference: `AR`, `AP`, `Inventory`, `GRNI`, `Bank`, `PdcReceivable`, `PdcPayable`, `FaCost`, `FaAccDep`, `IcReceivable`, `IcPayable`.
* Reversal of any document posts the mirror entry (sides swapped, `is_reversal = true`, linked) and reverses its subledger effects in the same transaction; reversal dates follow ADR-0026.
* "Expected cost" means the value used before the supplier invoice fixes the actual price (ADR-0008).

## 1. Account roles

| Role | Type | Notes |
|------|------|-------|
| `AR` | asset, control | receivables control per customer posting group |
| `AP` | liability, control | payables control per supplier posting group |
| `Inventory` | asset, control | per item posting group and optionally warehouse |
| `InventoryInTransit` | asset, control | transfers between warehouses |
| `InventoryConsignedOut` | asset, control | own stock at customers |
| `GRNI` | liability, control | goods received not invoiced (accrued purchases) |
| `LandedCostClearing` | liability, control | landed costs allocated before charge invoices |
| `Cogs` | expense | per item posting group |
| `Revenue` | revenue | per item posting group / category |
| `SalesReturns` | contra revenue | optional; default posts to `Revenue` |
| `DiscountGiven`, `DiscountTaken` | expense / income | settlement discounts |
| `OutputTax`, `InputTax` | liability / asset | per tax code |
| `WhtPayable`, `WhtReceivable` | liability / asset | withholding |
| `Bank`, `Cash`, `PettyCash` | asset, control | per bank/cash account |
| `ChequesUnderCollection` | asset, control | deposited cheques not yet cleared |
| `PdcReceivable`, `PdcPayable` | asset / liability, control | post-dated cheques |
| `BankCharges`, `InterestIncome`, `InterestExpense` | expense / income | |
| `FxGainRealized`, `FxLossRealized`, `FxUnrealized` | income / expense | unrealized may be one account or a pair |
| `CustomerDeposits` | liability | advances from customers (contract liability) |
| `SupplierAdvances` | asset | prepayments to suppliers |
| `UnbilledRevenue` | asset | when revenue is recognized at shipment |
| `InventoryAdjustment`, `CountVariance`, `Scrap`, `InventoryWriteDown` | expense | reason codes may override |
| `PurchasePriceVariance` | expense | standard costing |
| `AssemblyVariance` | expense | |
| `EmployeePayable` | liability | expense claims |
| `CommissionExpense`, `CommissionPayable` | expense / liability | |
| `BadDebt`, `WriteOff` | expense | |
| `RoundingDifferences` | expense/income | engine-generated |
| `FaCost`, `FaAccDep`, `FaAccImpairment` | asset, control | fixed assets |
| `DepreciationExpense`, `ImpairmentLoss`, `RevaluationSurplus` (OCI), `GainLossOnDisposal`, `FaClearing` | | |
| `AccruedExpenses`, `Prepayments`, `DeferredRevenue` | liability / asset | deferrals |
| `IcReceivable`, `IcPayable` | asset / liability, control | per counterpart company |
| `RetainedEarnings`, `CurrentYearEarnings`, `OpeningBalanceEquity` | equity | |
| `Suspense` | | manual journals only, must be zero at close |

## 2. Sales (order to cash)

| Document / event | Journal (Dr / Cr) | Subledger effects | Notes |
|------------------|-------------------|-------------------|-------|
| Quotation | none | none | |
| Sales order confirmation | none | reservation (stock), credit check, commitment (reporting) | Credit hold creates `wf_blocks`; no GL. |
| **Shipment** (stock items) | Dr `Cogs` / Cr `Inventory` at applied cost | SLE `sale_shipment` (−qty), SVE direct cost, applications; reservation consumed; serial → `sold`; lot quantities reduced | If company `revenue_recognition_point = shipment`: also Dr `UnbilledRevenue` / Cr `Revenue` (net). |
| **Sales invoice** (from shipment) | Dr `AR` gross / Cr `Revenue` net per line / Cr `OutputTax` per tax code | AR open item (invoice); tax lines; commission accrual if plan accrues at invoice: Dr `CommissionExpense` / Cr `CommissionPayable` | Document discount reduces `Revenue` (allocated to lines). With `UnbilledRevenue`: Dr `AR` / Cr `UnbilledRevenue` / Cr `OutputTax` instead of Revenue. |
| Sales invoice, direct (no shipment, stock items) | Both entries above in one journal | SLE + SVE + AR open item | |
| Sales invoice, service or non-stock lines | Dr `AR` / Cr `Revenue` (or the line's explicit revenue account) / Cr `OutputTax` | AR open item | |
| Sales invoice, tax inclusive prices | same; net = gross ÷ (1+rate) | | rounding per ADR-0005 |
| Sales invoice, reverse charge (customer accounts for tax) | Dr `AR` net / Cr `Revenue` net; no tax line, reverse-charge note printed | | |
| Sales invoice with deferred revenue schedule | Dr `AR` / Cr `DeferredRevenue`; monthly Dr `DeferredRevenue` / Cr `Revenue` | deferral schedule | |
| Prepayment (proforma) invoice paid | see Customer deposit | | |
| Recurring invoice | as sales invoice | | generated by schedule; may auto-post |
| **Customer receipt** (bank or cash) | Dr `Bank` amount received / Dr `BankCharges` fees / Dr `DiscountGiven` early-payment discount / Dr `WhtReceivable` tax withheld by customer / Dr or Cr `FxLossRealized`/`FxGainRealized` / Cr `AR` settled amount at booked value | settlements per invoice; bank transaction | FX = Σ(settled tc × settlement rate) − Σ(settled tc × booked rate). |
| Receipt on account (unallocated) | Dr `Bank` / Cr `AR` (open item kind `receipt_on_account`, credit) | AR open item (credit) | later allocation posts FX only |
| Allocation of on-account credit to invoice | Dr/Cr `FxGainRealized`/`FxLossRealized` for rate difference between the two items; no cash movement | settlement | if same rate: no journal, settlement only |
| **Customer deposit** (advance against order) | Dr `Bank` / Cr `CustomerDeposits` (+ Cr `OutputTax` where the regime taxes advances) | AR open item kind `deposit` (credit) on the deposits control | |
| Apply deposit to invoice | Dr `CustomerDeposits` / Cr `AR` (and tax already accounted is netted on the invoice) | settlement | |
| Overpayment | remaining amount stays as `receipt_on_account` credit | | refund posts Dr `AR` (credit item) / Cr `Bank` |
| **Sales return (RMA receipt)** | Dr `Inventory` at return cost / Cr `Cogs` | SLE `sale_return` (+qty) applied to the original shipment's cost; serial → `returned`; disposition to quarantine bin if inspection; lot status respected (recalled lots go to quarantine and are flagged) | Return cost policy: original (default), current, specified. |
| **Credit note** (from return or price adjustment) | Dr `Revenue` (or `SalesReturns`) / Dr `OutputTax` / Cr `AR` | AR open item (credit); tax lines; commission reversal | Automatically allocated against the original invoice when linked (settlement with no FX if same rate; FX otherwise). |
| Credit note without return (price/goodwill) | Dr `Revenue` / Dr `OutputTax` / Cr `AR` | AR open item (credit) | |
| Write-off (bad debt) | Dr `BadDebt` / Cr `AR` | settlement kind `write_off` | small-balance write-offs may use `WriteOff` or `RoundingDifferences` per threshold |
| Dunning fee | Dr `AR` / Cr `InterestIncome` | AR open item (debit adjustment) | |
| Commission payment | Dr `CommissionPayable` / Cr `Bank` | | commissions accrued at payment instead if plan says so |
| Drop-ship order (PO receipt + shipment) | Receipt: Dr `Cogs` / Cr `GRNI` (goods never enter stock); Invoice: standard | paired SLE `drop_ship` (+/−) for traceability with zero net stock | |
| Consignment out (stock moved to customer) | Dr `InventoryConsignedOut` / Cr `Inventory` | SLE `consignment_out` ownership `consigned_out` | no revenue |
| Consignment consumption report | Shipment + invoice from `InventoryConsignedOut` | | |
| Cancellation of remaining order quantity | none | reservation released, `qty_cancelled` | |
| Reversal of shipment | mirror: Dr `Inventory` / Cr `Cogs` | SLE reversal entry applied exactly to the original | |
| Reversal of invoice | mirror: Dr `Revenue`, Dr `OutputTax` / Cr `AR` | open item reversed (settled by reversal) | posts in the first open period if original is closed |

## 3. Purchasing (procure to pay)

| Document / event | Journal (Dr / Cr) | Subledger effects | Notes |
|------------------|-------------------|-------------------|-------|
| Requisition, RFQ, quote, PO | none | commitment (budget control), approvals | |
| **Goods receipt** (stock items) | Dr `Inventory` at expected cost (PO price incl. line discount, converted at receipt date rate) / Cr `GRNI` | SLE `purchase_receipt` (+qty), SVE `expected_cost`; lots/serials created; bins updated | |
| Goods receipt, standard cost item | Dr `Inventory` at standard / Dr or Cr `PurchasePriceVariance` / Cr `GRNI` at expected actual | SVE `direct_cost` (standard) + `variance` | |
| Goods receipt, non-stock/expense item | Dr expense account (line) / Cr `GRNI` | | |
| Goods receipt, fixed-asset item | Dr `FaClearing` / Cr `GRNI` | asset draft created | capitalised on invoice |
| **Supplier invoice** matched to receipt, price equal | Dr `GRNI` / Dr `InputTax` / Cr `AP` gross; WHT at invoice: Cr `WhtPayable`, AP reduced accordingly | AP open item; SVE `expected_cost_reversal` + `direct_cost` (same value); match result `matched` | |
| Supplier invoice, price differs (within tolerance or override approved) | Dr `GRNI` at receipt value / Dr or Cr `Inventory` for the difference on quantity still on hand / Dr or Cr `Cogs` for the difference on quantity already sold (via cost adjustment run) / Dr `InputTax` / Cr `AP` | SVE `direct_cost` at invoice price replaces expected; cost adjustment SVEs on applied outbound entries with reason "invoice price difference" | Standard cost: difference → `PurchasePriceVariance` instead of Inventory/COGS. |
| Supplier invoice exceeding tolerance | **no posting**; status `blocked`, `wf_blocks` kind `match_variance`; after override: as above | match result `price_variance`, override recorded | hard scenario 5 |
| Supplier invoice without receipt (expense/service) | Dr expense (line account) / Dr `InputTax` / Cr `AP` | AP open item | prepayment schedule if flagged: Dr `Prepayments` instead of expense, then monthly Dr expense / Cr `Prepayments` |
| Supplier invoice, reverse charge | Dr expense or `GRNI` / Dr `InputTax` / Cr `OutputTax` / Cr `AP` net | tax lines (both boxes) | |
| Supplier invoice for a fixed asset | Dr `FaCost` (via `FaClearing`) / Dr `InputTax` / Cr `AP` | FA transaction `acquisition` | |
| **Landed cost document** (allocation to receipts) | Dr `Inventory` (on-hand portion) / Dr `Cogs` (portion already sold, via cost adjustment run) / Cr `LandedCostClearing` (estimate) or Cr `AP` (when entered from the charge invoice) | SVE `indirect_cost` on each receipt SLE; cost adjustment SVEs on applied outbound entries with reason "landed cost <doc>"; allocation rows record on-hand/sold split | hard scenario 2; allocation basis value / weight / volume / quantity per charge |
| Charge invoice arriving after an estimated landed cost | Dr `LandedCostClearing` / Dr `InputTax` / Cr `AP`; difference between estimate and actual re-allocated as a new landed cost adjustment (Inventory/COGS vs Clearing) | AP open item; adjustment run | |
| Customs duty (customs authority as supplier) | as landed cost charge with `CUSTOMS` charge type; duty tax base rules per regime | | |
| **Supplier payment** | Dr `AP` settled at booked value / Dr `BankCharges` / Cr `DiscountTaken` / Cr `WhtPayable` (WHT at payment) / Dr or Cr `FxLossRealized`/`FxGainRealized` / Cr `Bank` | settlements; bank transaction | Built 4.7: one journal per payment; every settled item at its booked functional value (ADR-0031), the payment's own open item for what the lines do not use |
| Payment on account | Dr `AP` (open item kind `payment_on_account`, debit) / Cr `Bank` | AP open item (debit) | Built 4.7: the payment's own open item; applied later like a credit |
| **Supplier advance** (prepayment before goods) | Dr `SupplierAdvances` / Dr `InputTax` (where regime taxes advances) / Cr `Bank` | AP open item kind `advance` on the advances control | Built 4.7 (without the tax line: advances are not taxed in the regimes served yet); `SupplierAdvances` is a control on the payables subledger |
| Apply advance to invoice | Dr `AP` / Cr `SupplierAdvances` | settlement; FX realized if rates differ | Built 4.7 (`ap_settlements` kind `advance_application`, reversible) |
| **Supplier return** (return to vendor, invoiced or not) | Dr `GRNI` (subledger item: the return) / Cr `Inventory` at the receipt entries' exact cost at that moment (invoice re-pricing and landed costs included) | SLE `purchase_return` applied to the original receipt SLEs; the receipt line's returned quantity and cost | Built 4.6: one receipt per return; reversible while uncredited (goods back at the same cost) |
| **Debit note** (the supplier's credit for a return, or a credit on expenses) | Dr `AP` (subledger item: the note) / Cr `GRNI` (subledger item: the return) at the returned cost / the difference between the credit and that cost → `PurchasePriceVariance`; expense lines Cr their account; no WHT | AP open item of kind `debit_note` (negative, due at once); the return lines' credited quantity and amount | Built 4.6: `pur_invoices.kind = debit_note`, line kind `return`; reversal refused once applied |
| **Credit application** (debit note applied to an invoice) | Same rate on both items: no journal. Different booked rates: Dr `AP` (invoice item) / Cr `AP` (note item) in the company's currency, the difference to `FxGainRealized` / `FxLossRealized` | `ap_settlements` (kind `credit_application`); both open items' settled and remaining amounts | Built 4.6: same currency only; advances and payments (4.7) reuse the settlement table |
| Debit note without return (price claim) | Dr `AP` / Cr `Inventory` or `Cogs` (via cost adjustment) / Cr `InputTax` | AP open item (debit) | |
| Consignment in (supplier stock in our warehouse) | none at receipt (ownership `consigned_in`, unvalued) ; at consumption/sale: Dr `Inventory` / Cr `GRNI` then normal flows | SLE `consignment_in` then ownership transfer entries | |
| Reversal of receipt | mirror: Dr `GRNI` / Cr `Inventory` | SLE reversal applied to the original | blocked if quantity already consumed unless negative stock policy allows (then adjustment) |
| Reversal of supplier invoice | mirror; expected cost reinstated on the receipt | AP item reversed | |

## 4. Inventory

| Document / event | Journal (Dr / Cr) | Subledger effects | Notes |
|------------------|-------------------|-------------------|-------|
| Positive adjustment | Dr `Inventory` / Cr `InventoryAdjustment` (or reason-code account) at entered cost | SLE `positive_adjustment` (+), SVE direct cost | |
| Negative adjustment / scrap | Dr `InventoryAdjustment` or `Scrap` / Cr `Inventory` at applied cost | SLE (−), applications | |
| Opening stock | Dr `Inventory` / Cr `OpeningBalanceEquity` | SLE `opening` | import toolkit |
| Transfer, one step (no transit) | Dr `Inventory`(to) / Cr `Inventory`(from) at carried cost | SLE `transfer_out` + `transfer_in` paired | no journal if both warehouses map to the same account |
| Transfer, ship (two step) | Dr `InventoryInTransit` / Cr `Inventory`(from) | SLE out to transit warehouse | |
| Transfer, receive | Dr `Inventory`(to) / Cr `InventoryInTransit` | SLE in from transit | partial receipts allowed; shortages become adjustments with reason |
| Count variance (approved) | Dr/Cr `Inventory` vs `CountVariance` (reason code) | SLE `count_variance` per line | hard scenario 11: variance = counted − (frozen expected + movements since freeze) |
| Assembly / kit build | Dr `Inventory`(assembly) / Cr `Inventory`(components); Dr/Cr `AssemblyVariance` if output valued at standard | SLE consumption (−) and output (+) | kits sold as bundles: components issued at shipment instead (no build) |
| Standard cost change | Dr/Cr `Inventory` / Cr/Dr `InventoryWriteDown` (revaluation) for on-hand qty × (new − old) | SVE `revaluation` | |
| NRV write-down (IAS 2) | Dr `InventoryWriteDown` / Cr `Inventory` | SVE `revaluation` | reversal when NRV recovers, capped at original cost |
| **Cost adjustment run** (backdated receipt, late invoice, landed cost, revaluation) | For each affected outbound entry: Dr/Cr `Inventory` / Cr/Dr the entry's original offset (`Cogs` for shipments, `InventoryAdjustment` for adjustments, `InventoryInTransit` for transfers, `Inventory`(assembly) for consumption) | SVE `cost_adjustment` per affected entry with `reason` naming the trigger | hard scenario 1; posting date per ADR-0008 |
| Negative stock issue (policy allows) | Dr `Cogs` / Cr `Inventory` at expected cost, flagged | SLE `costed_at_expected` | next receipt triggers adjustment |
| Lot recall | none | lot status `recalled`; on-hand of that lot moves to quarantine status; open reservations released; report of customers shipped that lot (traceability) | hard scenario 12 |
| Serial repair (return → repair → resale) | return as above; repair costs (expense or capitalised to the serial via positive adjustment with cost); resale normal | serial status chain `sold → returned → in_repair → in_stock → sold` visible on one screen | hard scenario 13 |

## 5. Banking and cash

| Document / event | Journal (Dr / Cr) | Subledger effects | Notes |
|------------------|-------------------|-------------------|-------|
| Bank charge / interest (manual or from statement) | Dr `BankCharges` / Cr `Bank`; Dr `Bank` / Cr `InterestIncome` | bank transaction | statement-created transactions via reconciliation rules |
| Bank transfer, same currency | Dr `Bank`(to) / Cr `Bank`(from); with in-transit: via `ChequesUnderCollection`-style clearing account `BankTransfersInTransit` | two bank transactions | |
| Bank transfer, different currencies | Dr `Bank`(to) at received amount / Dr or Cr `FxLossRealized`/`FxGainRealized` / Cr `Bank`(from) at sent amount; charges as above | | |
| **PDC received** (from customer) | Dr `PdcReceivable` / Cr `AR` | AR settlement of the invoice(s) by the cheque's open item kind `pdc`; cheque `received`/`in_hand` | Company option "post on deposit only": no journal until deposit; the cheque then reduces AR at deposit. |
| Cheque deposited at maturity | Dr `ChequesUnderCollection` / Cr `PdcReceivable` | cheque `deposited`; deposit slip | |
| Cheque cleared | Dr `Bank` / Cr `ChequesUnderCollection` | cheque `cleared`; bank transaction | |
| **Cheque bounced** | Dr `AR` (new open item referencing the original invoice) / Cr `ChequesUnderCollection` (or `Bank` if already cleared and then returned); Dr `BankCharges` / Cr `Bank` for bank fee; optional Dr `AR` / Cr `InterestIncome` to re-charge the fee to the customer | cheque `bounced`; original settlement reversed (`reverses_settlement_id`), customer balance restored, dunning flag | hard scenario 10; history in `bnk_cheque_events` |
| Bounced cheque re-deposited or replaced | new cheque links `replaced_by_id`; postings as a new receipt | | |
| Cheque returned to customer (before deposit) | Dr `AR` / Cr `PdcReceivable` | settlement reversed | |
| **PDC issued** (to supplier) | Dr `AP` / Cr `PdcPayable` | AP settlement by cheque open item; cheque `issued` | |
| Issued cheque presented/paid | Dr `PdcPayable` / Cr `Bank` | cheque `paid`; bank transaction | from statement matching or manual |
| Issued cheque bounced (returned unpaid) | Dr `Bank` / Cr `PdcPayable` reversal of clearing if applied; Dr `BankCharges` / Cr `Bank` | cheque `bounced` | AP remains settled by PdcPayable until cancelled: cancellation posts Dr `PdcPayable` / Cr `AP` (reopens the invoice) |
| Petty cash replenishment | Dr `PettyCash` / Cr `Bank` | | |
| Petty cash expense voucher | Dr expense / Dr `InputTax` / Cr `PettyCash` | | receipts attached |
| Expense claim approved | Dr expense lines / Dr `InputTax` / Cr `EmployeePayable` | AP open item on the employee partner | |
| Expense claim reimbursed | Dr `EmployeePayable` / Cr `Bank` | settlement | |
| Cash rounding on cash receipts (IQD 250 rule) | Dr/Cr `RoundingDifferences` for the rounding amount | | applied only for cash payment methods |

## 6. Multi-currency

| Event | Journal (Dr / Cr) | Notes |
|-------|-------------------|-------|
| Any foreign-currency document | lines converted at the document's rate type and posting date; fc and rc stored on each line | rate override requires reason and is audited |
| **Realized FX on settlement** | Gain: Dr `AR`/`AP` side neutral, Cr `FxGainRealized`; Loss: Dr `FxLossRealized`. Concretely on a receipt: Dr `Bank` (settlement fc) / Cr `AR` (booked fc of settled tc) / Dr or Cr FX for the difference | hard scenario 3: two instalments at different rates each realize their own difference; bank fees post to `BankCharges` in the same journal |
| **Unrealized FX revaluation, open items** (AR, AP, IC) | Dr `AR` / Cr `FxUnrealized` for gains (Cr `AR` / Dr `FxUnrealized` for losses), one line per open item with subledger ref; entry flagged auto-reverse on the first day of the next period | revaluation lines are settlements of kind `revaluation` so control still equals subledger; the reversal is a mirror entry on the reversal date |
| Unrealized FX revaluation, bank and cash | Dr/Cr `Bank` / Cr/Dr `FxUnrealized`, permanent (no reversal) by default; `booked_fc_balance` updated | company option `reversing` behaves like open items |
| Cross-rate settlement (invoice EUR, bank USD, functional IQD) | Dr `Bank` at USD amount converted to IQD at the payment's bank rate / Cr `AR` at EUR settled × booked EUR→IQD rate / FX difference | three-currency case in ADR-0017 |
| Reporting-currency rounding | `RoundingDifferences` line in rc only | engine-generated |

## 7. Fixed assets

| Event | Journal (Dr / Cr) | Notes |
|-------|-------------------|-------|
| Acquisition from supplier invoice | Dr `FaCost` / Dr `InputTax` / Cr `AP` (via `FaClearing` when receipt precedes invoice) | FA transaction `acquisition` |
| Acquisition from stock (item capitalised) | Dr `FaCost` / Cr `Inventory` | SLE negative adjustment with reason `capitalised` |
| Addition (improvement) | Dr `FaCost` / Cr `AP` or `Bank` | |
| Depreciation (monthly run per book that posts to GL) | Dr `DepreciationExpense` (dimensioned by asset location/cost centre) / Cr `FaAccDep` | FA transaction per asset per period; schedule updated |
| Revaluation upward (IAS 16 revaluation model) | Dr `FaCost` / Cr `RevaluationSurplus` (OCI); accumulated depreciation restated or eliminated per policy | reversal of a prior impairment goes to P&L first |
| Impairment | Dr `ImpairmentLoss` / Cr `FaAccImpairment` (against `RevaluationSurplus` first if one exists for the asset) | |
| Disposal / sale | Dr `Bank` or `AR` proceeds / Dr `FaAccDep` / Dr `FaAccImpairment` / Dr or Cr `GainLossOnDisposal` / Cr `FaCost`; any `RevaluationSurplus` for the asset transfers to `RetainedEarnings` | tax on sale proceeds via invoice |
| Scrapping | as disposal with zero proceeds | |
| Transfer between branches/cost centres | Dr `FaCost`(new dims) / Cr `FaCost`(old dims), same for `FaAccDep` | dimension change only |
| Intercompany asset transfer | disposal in A at NBV (or agreed price) + acquisition in B; IC receivable/payable | eliminated in consolidation |

## 8. Journals, deferrals, budgets, intercompany, closing

| Event | Journal (Dr / Cr) | Notes |
|-------|-------------------|-------|
| Manual journal | as entered; balanced; control accounts blocked unless the journal targets a subledger item explicitly (creating an open item adjustment) | approval workflow optional |
| Recurring journal | generated from template on schedule; may require review | |
| Reversing journal / accrual | entry with `auto_reverse_on`; the reversal is posted automatically on that date as a mirror entry | e.g. Dr `Expense` / Cr `AccruedExpenses`, reversed next period |
| Prepayment amortisation | Dr expense / Cr `Prepayments` per period from the deferral schedule | |
| Deferred revenue recognition | Dr `DeferredRevenue` / Cr `Revenue` per period | |
| Shipped-not-invoiced accrual (close checklist, when revenue point = invoice) | Dr `UnbilledRevenue` / Cr `Revenue` for shipped quantities not invoiced at period end; auto-reversing | keeps cut-off right without changing the default flow |
| Received-not-invoiced review | no posting: `GRNI` already carries the accrual | report reconciles `GRNI` to uninvoiced receipts |
| Budget | none | budget vs actual is reporting; budget control raises blocks |
| **Intercompany sale** (A sells to B) | A: Dr `IcReceivable`(B) / Cr `Revenue` / Cr `OutputTax` (if applicable); shipment posts COGS in A. B (mirrored purchase): Dr `Inventory` or expense / Dr `InputTax` / Cr `IcPayable`(A) | hard scenario 8; each company in its own functional currency at its own rate for the same date and rate type |
| Intercompany settlement | A: Dr `Bank` / Cr `IcReceivable` (+ realized FX); B: Dr `IcPayable` / Cr `Bank` (+ realized FX) | |
| Intercompany recharge (cost sharing) | A: Dr `IcReceivable` / Cr expense (recovery); B: Dr expense / Cr `IcPayable` | |
| **Consolidation eliminations** (group ledger only) | Dr `IcPayable` / Cr `IcReceivable` (balances); Dr IC revenue / Cr IC expense; Dr `Revenue` / Cr `Inventory` for unrealized profit in inventory (when enabled); Dr equity of subsidiary / Cr investment in subsidiary (and non-controlling interest); CTA to equity for translation differences | never posted to company GLs |
| **Year-end close** | Dr every P&L account with a credit balance / Cr every P&L account with a debit balance, net to `RetainedEarnings` (per dimension set when `by_dimension`); flagged `is_closing_entry` in the closing period | hard scenario 16 |
| Year reopen | mirror of the closing entry (`closing_of` link reversed), audited with reason; re-close posts a new closing entry | |
| Opening balances (go-live) | Dr/Cr each balance sheet account / `OpeningBalanceEquity`; AR/AP opening items posted as individual open items against the control (so aging works); stock via opening SLEs; fixed assets via acquisition + accumulated depreciation transactions; `OpeningBalanceEquity` must be zero after migration | import toolkit validates that the sum is zero and subledgers equal controls before allowing go-live |

## 9. Reversals: general rules

1. A reversal posts a **mirror entry**: same accounts and amounts, sides swapped, all three currencies copied (not recomputed), `is_reversal = true`, `gl_entry_links(reverses)`.
2. Subledger effects are reversed in the same transaction: open items get a reversing settlement; stock entries get a reversing SLE applied exactly to the original entry (so cost is identical); FA and bank transactions get reversing rows.
3. Reversal date = original posting date if that period is open for the module and the user; otherwise the first open period's start date, and the reversal carries `reason` and a reference; both documents show the link.
4. A reversed document cannot be reversed again; corrections are new documents linked with `corrects`.
5. Auto-reversals (accruals, FX revaluation of open items) use the same mechanism with `is_auto_reversal = true` and are scheduled by the worker on the reversal date; if the reversal period is not open, the job waits and alerts.

## 10. Rounding lines

The engine appends a `RoundingDifferences` line whenever functional or reporting totals of an entry differ from zero after per-line conversion; the line has zero transaction-currency amount and is flagged `is_rounding`. Tax rounding follows the document's `tax_rounding_mode`; cash rounding on cash payment methods posts the rounding amount to `RoundingDifferences` with the cash payment.

## 11. Scenario-to-rule index

| Hard scenario | Rules used |
|---------------|-----------|
| 1 Backdated receipt after sale | §3 goods receipt, §4 cost adjustment run, ADR-0008 |
| 2 Late freight and customs | §3 landed cost document, charge invoice after estimate, §4 cost adjustment run |
| 3 EUR invoice, two instalments, fees, revaluation | §2 customer receipt, §6 realized FX, unrealized revaluation with auto-reverse |
| 4 Two users, last unit | §2 shipment with row locks (ADR-0009); negative-stock policy row in §4 |
| 5 Invoice above tolerance | §3 supplier invoice exceeding tolerance, override |
| 6 Credit limit | §2 sales order confirmation (block), override logged |
| 7 Closed period | §9 reversal date rule, ADR-0026 |
| 8 Intercompany in two currencies | §8 intercompany sale, settlement, eliminations |
| 9 Cartons, pieces, dozens | UoM conversion at entry (DOMAIN_MODEL §8), all stock postings in base unit |
| 10 Post-dated cheque bounce | §5 PDC received, deposited, bounced |
| 11 Count during operations | §4 count variance with freeze snapshot |
| 12 Return from recalled lot | §2 sales return with quarantine disposition, §4 lot recall |
| 13 Serial sold, returned, repaired, resold | §2 return, §4 serial repair |
| 14 Price breakdown | pricing engine (ADR-0030); posting uses the resulting net and tax |
| 15 Subledgers equal controls at any date | every control-account row above carries a subledger ref; invariant harness |
| 16 Year-end close and reopen | §8 year-end close, reopen |
| 17 Gross margin report | `fact_sales_lines` cost from value entries (including adjustments), ADR-0021 |
| 18 Tenant isolation | not a posting rule; ADR-0004 |
