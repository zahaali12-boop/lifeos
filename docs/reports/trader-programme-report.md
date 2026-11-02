# Trader programme — report to the founder

Date: 2026-11-02. Branch: `claude/efficient-request-ajd9zy`. Slices T1–T7 of the Trader programme (ADR-0035, ROADMAP "Trader programme") are done; this report closes the programme and I pause here for your review.

## 1. What was built

A **trader edition** of the same product for a small import business run by a non-accountant: one engine, one database, one API, and a second face. The edition is a workspace setting (Settings → Edition); switching changes what the client shows, never what the server records, and a trader who grows switches back and finds every record where it was.

* **T1, the shell and the offers.** A flat menu of eight plain-words screens (Home, Offers, Shipments, Prices, Sales, Money, Health, Stock, plus Customers, Suppliers, Items, Settings). Offers: what to buy, the suppliers in China, their offers with price, minimum quantity, note, delivery and shipping; compared landed in the company's currency at the day's rate; the recommendation with its reasons and the saving a split would bring; "order from this supplier" makes the purchase order.
* **T2, the shipments.** The order as a dated journey (ordered, in production, shipped, at port, customs, received); the costs along the way in their currency at the rate actually paid; what was paid for the goods and the effective rate; the landed cost per unit before the goods arrive; hints (overdue, no costs, nothing paid, the rate moved, invoice missing, stale). Arrival posts the goods receipt and the landed costs; the supplier's invoice is recorded into the payable.
* **T3, the prices.** The pricing board: each item's landed cost at the rate paid, its cost to buy again at today's rate (entered under Rates), the current selling price and margin, the suggested price at the target margin rounded up to an increment, alerts (selling below cost, below today's cost, margin below target, no price, no rate), and "apply" into the sales price list.
* **T4, sales and money.** A sale in one step (order, shipment, invoice, posted); each invoice's state in plain words; a payment into the cash box or the bank. Money: who owes you and whom you owe, how late, "chase first" and "pay next" in plain words, sales by month.
* **T5, health.** Is the project alive: the month's income statement in plain words read from the books (sold, what the goods had cost, profit on the goods, expenses by name, what is left), the month before and six months of results; fixed costs, the sales a month needs to break even, where this month leads at its pace, the cash and how long it lasts, what is owed each way and the money position; warnings; the shipments on the way with their money; what sold by item and who bought; what to reorder.
* **T7, the home.** The first screen of the day in plain words: sold today, whether the month pays, the cash and how long it lasts, what is owed each way, what is on the way, what to do today (each line opening the screen that acts on it), and the four things a trader does most.
* **T6, stock and counting on the phone.** Stock: what the store holds and what it is worth at cost. A count in three steps: start, count on the phone with the camera (barcodes) or type the counts on the screen, finish: the differences are recorded and the stock set right, the items not counted stay as they are.

Everything is in English and Arabic, right-to-left, and runs on the brother's PC through Docker with the auto-update (`deploy/windows`).

## 2. How to see it working

1. Update the PC (`update.bat` runs every 30 minutes; double-click it to update now) and open http://localhost:5173.
2. Settings → Edition → Trader. The top bar becomes the trader's row.
3. Offers → compare two suppliers → order from the recommended one → Shipments → follow the order to arrival, add the freight and what was paid → Prices → apply the suggested prices → Sales → sell → record the payment → Money → Health → Stock → start a count.
4. On the phone (same Wi-Fi): put the PC's address in `.env` as `QUICKER_LAN_HOST=192.168.x.x`, run `update.bat --force`, then open `http://192.168.x.x:5173/m` on the phone, sign in, add it to the home screen, choose the store and press Count. Scan the barcode with the camera (or type the item code), type the quantity. Back on the PC, Stock → Finish.

The demo workspace (owner@quicker.example / DemoPass2026!) can be switched to the trader edition the same way.

## 3. Test results

Every slice shipped with API tests against a real PostgreSQL and the browser journey `trader-journeys.spec.ts` in English and Arabic (axe accessibility on every screen). At the close: Purchasing 23/23, Identity 28/28, Pricing 17/17, Sales 37/37, Trader 2/2, the stock-count tests 2/2 (the existing one and the new finish), Reporting 7/7, Banking 12/12, Receivables 6/6, Payables 4/4, Accounting 21/21, Architecture 7/7, Schema 10/10, web unit 47/47; the trader journey passes end to end; CI green on every pushed slice.

## 4. Decisions made (recorded in ADR-0035 and A-186 to A-191)

* One product with two faces, never two products: every trader screen is a composition over the modules' contracts; a new experience-tier module `Quicker.Trader` holds the pricing board and the health read and owns no tables.
* The books stay the books: a sale, a receipt, a shipment's arrival, a count's differences all post through the same services and the same posting engine as in the full edition; the trader's income statement is the ledger grouped exactly as the shipped profit and loss, so the two agree to the dinar.
* Settings the trader edits in place (target margin, rounding increment, fixed costs, cover days) are organization settings, so an admin sees them under Settings too.
* Warnings and hints are codes with values; the screens word them, so the server stays language-free and the same codes can drive notifications later.
* The phone is the installed web scanner over the Wi-Fi, not a store app.

## 5. Known gaps (listed in `docs/PROGRESS.md`)

A split order placed by the program; offers in a foreign currency need a spot rate of the day under Rates; costs that arrive after the goods were received; partial arrivals; paying the supplier from the shipment screen; prices per customer group or quantity break; a trader-worded customer page; discounts and returns from the sales screen; receipts on account; profit by item; a cash forecast by week; a lead time per supplier in the reorder; an expense recorded from the health screen; a phone shell of its own; barcode label printing.

## 6. What is next

Your review of the six screens with your brother, and the answers that shape the next programme: whether the trader edition goes to other traders (then the customer page and the label printing come first), or whether the iPhone build is wanted before that. The open questions in `docs/ASSUMPTIONS.md` (Q2, the legacy Life OS files at the root; the ZATCA OTP) still wait for you.
