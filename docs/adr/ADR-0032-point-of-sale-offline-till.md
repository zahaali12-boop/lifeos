# ADR-0032: Point of sale: an offline-capable till that posts through the engines on sync

Date: 2026-10-31. Status: accepted. Founder programme item 7 (ROADMAP "Founder programme"), ASSUMPTIONS A-180.

## Context

Shops in Iraq and the Gulf sell for cash at a counter where the connection comes and goes. The brief asks for a point
of sale that keeps selling without it, and the working rules ask that every financial effect goes through the posting
engine and that the books are always right. The sales module already has orders, shipments, invoices and returns, but
a till is a different shape of work: one screen, a barcode, a few taps, cash and change, a receipt in seconds, and a
cashier who should not see the back office.

## Decision

1. **A new process module, `Quicker.Pos`** (tier 3, ARCHITECTURE §6), with three documents and no new engines:
   * a **register** (`pos_registers`) is the till's configuration: company (and branch), the warehouse (and bin) it
     sells from, the cash box its cash goes into, the bank account card payments settle into, the price list and
     channel `pos` it prices on, the walk-in customer sales are booked to when none is named, whether it sells on
     account, whether the cashier may change prices and give discounts, the increment cash totals round to (the
     company's setting for the currency when none), and the receipt's footer;
   * a **session** (`pos_sessions`, `SES-{yyyy}-{seq:5}`) is a cashier's shift on a register: opened with a float,
     one open per register at a time, closed with the cash counted against the float plus the cash the orders took;
   * an **order** (`pos_orders`, `pos_order_lines`, `pos_payments`, `POS-{yyyy}-{seq:6}`) is a sale or a refund the
     till synced, immutable once posted.
2. **The till prices from a cached catalogue and the server books what it recomputes.** The catalogue a till caches
   is built by the server: every sellable item in its sales unit (lot- and serial-tracked items left out), priced
   through the pricing engine for the register's price list, channel and walk-in customer at quantity one, taxed by
   the tax engine as a sale to that customer today, with its barcodes, plus the register's rules and the arithmetic
   the till must mirror (currency decimals, the company's rounding mode, the regime's rounding level, whether prices
   include tax, the cash rounding increment). The till computes each order's totals exactly as `TaxCalculator` does
   and shows them on the receipt. On sync the server resolves the lines again, prices them to record the list price
   beside the price charged (and to refuse a change the register does not allow), taxes them with the codes the
   catalogue pinned, rounds cash when the whole order is paid in cash, and **refuses the order when its total differs
   from what the receipt showed** (`pos.totals_mismatch`): a stale catalogue is caught, never booked. The device
   keeps the refused order with the reason for the cashier or the back office to act on.
3. **Posting on arrival, in one unit of work, through the existing engines** (POSTING_RULES §2 "POS order"): the
   journal (revenue per line keyed by item and customer posting groups, output tax per code, `RoundingDifferences`
   for the cash rounding, the cash box and card account as `Bank` subledger lines, `AR` for the part on account),
   the stock movement through `IInventoryPosting` (`sale_shipment`, or `sale_return` for a refund, the costing
   engine adding the cost of sales), the bank transactions, the receivable of a credit sale (due on the order date),
   the tax ledger, and the e-invoice submission when the regime names a scheme. The order date is the order's own
   local date, so a sale made offline on Monday and synced on Tuesday posts on Monday.
4. **Idempotent sync.** Every order carries the till's own id (`device_order_id`, unique per tenant). An order sent
   again, because a sync was cut off before the answer arrived, is answered with the order already posted and
   `replayed: true`, so the queue can always be retried.
5. **Refunds give back what a sale sold.** A refund names the sale (by device id), copies each line's price, discount
   and tax code from the sale's line, and may not exceed what is left of it (`pos.refund_exceeds_sale`); the stock
   comes back as a sale return at the engine's cost, the money goes out the same tenders. A refund without a sale is
   not offered in this version.
6. **Session close books the cash difference.** Expected cash is the float plus the cash taken net of change and
   refunds; a difference from the count is posted on the cash box against `Suspense` (over: Dr cash / Cr suspense;
   short: Dr suspense / Cr cash) with a bank transaction, so the box's book balance is what was counted and the
   accountant clears the suspense. Closing needs the device's queue empty, so the takings are complete.
7. **The till is a route group of the web app (`/pos`)**, not a separate application: the same session, the same
   service worker precaching the app shell, the catalogue and this device's orders kept in local storage like the
   scanner's queue (roadmap 3.8). It sells, prints a device receipt (the server's number and the e-invoice QR arrive
   on sync; the full receipt with the QR prints from the back office through the print pipeline), shows what waits
   to sync, reprints and refunds. Opening and closing a session and refreshing the catalogue need a connection.
8. **E-invoicing.** An order under a scheme is handed to the adapter through `IEinvoiceSource` for `pos_order`: a
   sale is an invoice, a refund a credit note of the sale; a named customer with a VAT registration makes it a
   standard invoice, otherwise a simplified one; payment means 10 (cash), 48 (card) or 30 (on account). The
   submission is a job (`pos.orders.einvoice`) like the sales invoice's, so a sync never waits on the scheme.
9. **Roles.** A `cashier` template sells, opens and closes sessions, reads customers and items and prints; the
   back office (registers, sessions, orders) is read with `pos.*.read` and set up with `pos.register.manage`.

## Consequences

* The books never depend on the device: the server recomputes everything and refuses what it cannot reconcile.
  The price of that safety is a refused order after a price change until the till refreshes its catalogue; the
  back office sees it on the device's list, and the catalogue refresh is one tap.
* Negative stock follows the company's and warehouse's policy, as for any shipment: an offline sale of the last unit
  twice fails on sync where negative stock is not allowed, and the queue shows it.
* A till sells in the company's currency only; a multi-currency counter (dollars and dinars in one drawer) is a later
  slice (A-180 deferrals), as are lot- and serial-tracked items at the till, promotions and quantity breaks applied
  offline, a customer display, cash drawer and printer integrations beyond the browser's print.
