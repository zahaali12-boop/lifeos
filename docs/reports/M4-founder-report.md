# M4 Procure to pay — report to the founder

Date: 2026-09-23. Branch: `claude/efficient-request-ajd9zy`. Slices 4.0–4.9 are done; this report closes the milestone and I pause here for your review.

## 1. What was built

The whole buying side, from "we need something" to "the supplier has been paid", with every financial step going through the posting engine and every subledger reconciling to its control account.

* **Approvals (4.0).** A workflow engine any document can use: definitions per document type, rules written in a small safe expression language (`amount_in('USD') > 5000 and department = 'IT'`), steps with named approvers or roles, delegation, escalation by schedule, an approvals inbox, and blocks with overrides. With no definition, a document approves itself, so small companies are not slowed down.
* **Suppliers (4.1).** One partner record per legal person, with a supplier account per company: currency, payment and delivery terms, posting group, withholding code, price and quantity tolerances, lead time, holds. Bank accounts are stored encrypted, and IBANs are checked.
* **Requisition to order (4.2).** Requisitions with approval; RFQs sent to suppliers by email, quotes recorded and compared side by side, one awarded into an order; blanket agreements with releases; purchase orders with approval, email, change history and budget commitments.
* **Goods receipts (4.3).** Receipts against orders (partial, over-receipt tolerance, bins, lots, serials) post stock at the expected cost with the goods-received-not-invoiced (GRNI) account on the other side. Receiving also works on the phone scanner.
* **Supplier invoices (4.4).** Three-way match against receipts, two-way against service lines, expense invoices. A price or quantity outside tolerance blocks the invoice until someone authorised overrides it. Price differences re-cost the stock (FIFO, average) or go to purchase price variance (standard). Withholding tax is taken at invoice.
* **Landed costs (4.5).** Freight, customs, duty, insurance and handling are allocated across receipt lines by value, weight, volume or quantity. When the charges arrive after part of the goods has been sold, the cost splits between the stock still on hand and the cost of goods sold.
* **Returns and debit notes (4.6).** Goods go back at exactly the cost they came in at. The debit note clears GRNI and settles against the invoice.
* **Payables and banking (4.7).**
  * Open items for every invoice instalment, debit note, advance and payment on account.
  * Aging at any date.
  * Holds.
  * Payment proposals that pick what is due, taking early-payment discounts and withholding.
  * Bank and cash accounts.
  * Supplier payments: partial, early-payment discount, withholding tax at payment, bank charges, realised FX, remainder on account, reversal.
  * One change to the posting engine: a line can carry a fixed company-currency amount. That is what lets a EUR invoice paid at a different rate book its realised FX exactly (decision record ADR-0031).
* **Supplier intelligence (4.8).** Price history per item and supplier across orders, invoices and quotes; lead times (average, median, share on time); a scorecard with weights you configure (on time, quantity kept, price within tolerance, invoices matched first time) and grades A–D.
* **Demo data (4.9).** Per company:
  * Eleven months of closed purchase orders, which give the price history.
  * A live month with an on-time supplier, a late supplier with a return and a debit note, and an unpaid one, so the aging has something to show.
  * Freight landed two weeks after a receipt, payments from an operating bank account, and an RFQ with three quotes.
  * In the warehouse: transfers, counts, and a replenishment planner run.

Every new screen exists in English and Arabic, right to left, and is checked for accessibility.

## 2. How to see it working

```
make up          # everything, including the demo tenant (about seven minutes the first time)
open http://localhost:5173   # owner@quicker.example / DemoPass2026!
make demo        # rebuild the demo tenant
```

Things worth clicking through (the keyboard shortcut is in brackets):

1. **Supplier intelligence** (`g ]`): pick Al-Rafidain Trading. The price history shows prices creeping up about one percent a month for a year, and Jebel Ali pricing about 4 % above Turkish Foods. The scorecard ranks the on-time supplier above the late one. Change the tolerance days in the settings and watch the grade move.
2. **Purchase orders** (`g 7`): a year of closed orders per supplier; this month's orders are received, partly received or open.
3. **Goods receipts** (`g 0`) and **Supplier invoices** (`g .`): open an invoice to see its match lines against the receipt and its payables tab.
4. **Landed costs** (`g ,`): freight posted two weeks after the goods arrived, allocated across the receipt lines.
5. **Returns** (`g ;`): ten units back to the late supplier, with the debit note that settled against the invoice.
6. **Payables** (`g /`): open items and the aging tab, with the unpaid supplier's invoice and the half-paid one still open, next to the demo books' utility and consulting invoices.
7. **Payment proposals** (`g -`): propose everything due by month-end, approve, and turn it into a payment.
8. **Supplier payments** (`g [`) and **Bank & cash accounts** (`g =`): the payments out of the operating account and its balance.
9. **RFQs** (`g 6`): three quotes for next month's restock, ready to compare and award.
10. **Approvals** (`g 1`) and **Workflows** (`g 2`): add a rule such as "orders over 5,000,000 IQD need the approver role", submit an order as the purchaser (`purchasing@quicker.example`), and approve it from the inbox as Layla (`approvals@quicker.example`).

## 3. Test results

* **.NET:** 246 tests, every one against a real PostgreSQL database.
  * M4 added workflow, partners, purchasing, payables and banking suites.
  * The milestone's hard scenarios are among them: 2 (late landed cost split between stock and COGS), 5 (price tolerance blocks the invoice until an override), and 3 on the payables side (a EUR invoice in a USD company paid in two instalments at different rates with bank fees).
  * So is 15 on the payables side: aging at any date equals the AP control account.
  * Every scenario ends with the invariant harness, which now has eleven checks, including GRNI against receipts, payables against open items, and bank against bank transactions.
* **Demo test:** seeds the tenant twice and checks:
  * the counts repeat;
  * the harness passes;
  * the purchasing, warehouse and planning data are all there.
* **Web:** 8 Playwright journeys (admin, accounting, inventory, phone scanner, workflow, supplier, purchasing, payables), seven of them also checking the Arabic right-to-left screens, with axe on every screen.
* **API contract:** compatible. The three paths that moved from Purchasing to Payables in 4.7 are recorded.

## 4. Decisions made along the way

All are recorded in `docs/ASSUMPTIONS.md` (A-106…A-115) and ADR-0031. The ones you may want to look at:

* **No workflow definition means auto-approve** (A-106). Admins add approval rules when they want them; nothing is blocked out of the box.
* **Tolerances live on the supplier account** (A-110): one price and one quantity tolerance per supplier and company. A separate rule table by item category waits for a real need.
* **Payables is its own module** (A-113), and settlements are rows that are never edited: a reversal is a mirror row. Aging at any past date is therefore exact.
* **A posting line may fix its company-currency value** (ADR-0031). This is the only change M4 made to the posting engine, and it is what makes realised FX on payments exact.
* **Demo "year of purchases"** (A-115). The demo books close every past month, so the past eleven months hold orders only, which don't post. Everything that posts happens in the open current month.

## 5. Fixes found in this milestone that you should know about

These are all in `docs/PROGRESS.md`, "Post-milestone fixes".

* **A second company could not number its receipts, invoices, returns, landed costs, payments or proposals.** Their numbering series used a code shared by the whole workspace, and every test used one company. The demo seed caught it. Fixed, with a regression test.
* **`make demo` failed on a fresh database after 4.7**, because the demo suppliers had no partner records. Fixed.
* **The demo test had been failing unnoticed** since 4.3: it expected a fixed number of invariant checks. It now reads the registered list.
* **The M3 screenshot review found four rough edges**, all fixed:
  * a raw item ID and raw codes in the cost-run list;
  * expired lots shown as "Active";
  * a replenishment planner with nothing to suggest;
  * empty transfer and count screens in the demo.
* **Smaller UI issues:** Arabic dates scrambled in grids, settlement types shown as codes, and a warning colour that failed contrast. All fixed.

## 6. Known gaps

The full list with reasons is in `docs/PROGRESS.md`.

* **Tax is zero** on orders and invoices until the tax engine (M5). The columns and totals are already there.
* **Budget control** (blocking an order over budget) waits for M6. Commitments are already recorded.
* **Netting payables against receivables** waits for the receivables module (M5).
* **Cheques** are a payment method only. Cheque books and post-dated cheques come in 6.2; bank reconciliation comes in 6.x.
* **Payments** go out in the payment currency or the company currency; a third currency waits for 6.1.
* **The month-end FX revaluation** of open foreign-currency items (the second half of hard scenario 3) is slice 6.5.
* **Tolerance rules by item category**, the manager-chain approvers, and dynamic owners are not built yet.
* **Demo:** posted purchasing documents exist only in the current month (A-115). The demo has no requisitions, blanket agreements, foreign-currency purchases or match exceptions yet.

## 7. What you can do now

1. **Review and merge:** ask for a pull request from `claude/efficient-request-ajd9zy` to `main` (none exists yet).
2. **Set your approval rules:** which documents need approval, above what amounts, and by whom. The default is none.
3. **Set supplier tolerances:** the price and quantity tolerances beyond which an invoice is blocked. The default is zero, meaning any difference blocks.
4. **Q9** in `docs/ASSUMPTIONS.md` (the Iraq statutory code list) is still open; nothing built depends on it.

## 8. What is next

M5 Order to cash: sales documents, the pricing engine ("why this price"), receivables with FX, the tax engine, credit limits and holds, and customer returns. As you decided, M5 and the other design-heavy work are reserved for the later, more capable pass (`docs/PROGRESS.md`, "Next", item 2). Routine additive work (reports, screens over existing services, demo data, fixes) can continue in the meantime whenever you ask.
