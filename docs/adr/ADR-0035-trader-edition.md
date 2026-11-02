# ADR-0035: The trader edition: one engine, a second shell in plain language

Date: 2026-11-02. Status: accepted. Founder programme of 2026-11-02 (ROADMAP "Trader programme"), ASSUMPTIONS A-186.

## Context

The founder asked for "another version of the program" for a small business that imports car spare parts from China
and is run by someone who is not an accountant and keeps his records in Excel and on paper. He listed what it must do:
compare purchase offers and pick the best by price, specification and minimum order quantity; turn a purchase into an
order, an invoice and a payable with suggestions; price by the exchange rate paid, warn of losses and of market rate
moves; a full sales side with customers, invoices, partial payments, reports, charts and strong suggestions; a
financial analysis page with break-even; receivables and payables aging; warehouses with easy counting; and reports a
non-accountant reads (revenue, expenses, net profit, the shipments on the way). No reading of photos or PDFs, exchange
rates entered by hand, and it runs on his brother's PC with the same Docker install and automatic updates; a phone
version later.

Every one of those needs is already a capability of Quicker: requests for quotation and supplier quotes, purchase
orders, receipts and invoices, landed costs, price lists and pricing rules, sales documents, receivables and
collections, aging, stock and counts, the statements and the report builder. What is missing is not accounting but
*fit*: the full product shows a trader eighty screens behind module menus in the vocabulary of an ERP. Building a
separate program would mean a second set of books and a second posting engine, which the working rules forbid; it
would also throw away the tests and the invariants that make the books right.

## Decision

1. **One engine, one database, one API; the edition is a setting of the workspace.** `control.tenants.settings`
   carries `edition`, one of `full` (default) or `trader` (`TenantEditions`). The tenant summary of the sign-in token
   and of `/api/v1/me` carries it; `GET`/`PUT /api/v1/tenant/edition` read and switch it under
   `identity.tenant.manage`. Switching changes what the client shows, never what the server records: the same
   endpoints, permissions, postings, invariants and reports serve both editions, and a workspace can switch back and
   forth without losing anything. There is no trader-only table; where the trader needs a field the engine lacks,
   the field is added to the module for every edition.

2. **The trader shell is a flat menu in plain words.** When the session's edition is `trader`, the web shell shows one
   row of links in place of the module menus: Home, Offers, Orders, Suppliers, Customers, Invoices, Receipts, Items,
   Stock, Rates, Settings (more arrive with each slice: Shipments, Pricing, Money, Reports). The drawer, the shortcuts
   and the command palette follow the same list. Every link opens either an existing screen or a trader screen that
   wraps a module's service in the trader's vocabulary ("offers" are supplier quotes on a request for quotation,
   "what to buy" is its title, "ordered" is awarded). Labels are everyday words in English and Arabic, and the
   recommendation texts name money, not methods.

3. **Offers: the comparison decides and explains.** An offer line carries the supplier's minimum order quantity and a
   note on the specification (`pur_supplier_quote_lines.moq`, `.notes`, V0072). The comparison ranks the quotes that
   price every line and whose minimums the request reaches first, then the complete ones, then by the landed total in
   the company's currency at the spot rate of the day, then by lead time; each other quote says why it is not first
   (missing items, a minimum not reached, dearer by a percentage, or cheaper but not takeable as offered). It names the
   cheapest feasible offer per line, recommends the first-ranked quote with reason codes the client words
   (`cheapest_landed`, `complete`, `meets_moq`, `fastest`, `only_offer`), and gives the saving against the next usable
   offer and the saving of splitting the order line by line. A request is awardable from a draft: a trader records the
   offers that reached him on the phone and orders without sending a request out; sending remains for those who do.

4. **Shipments: the order's journey, posted by the existing services.** A shipment (`pur_shipments`, one per purchase
   order) carries the stages as dated events, the costs along the way in their own currency at the rate actually paid,
   and what was paid for the goods in the company's currency; from these it shows the effective exchange rate and the
   landed cost of each unit before the goods arrive. Arrival creates and posts the goods receipt and a landed-cost
   document from the recorded costs through the receipt and landed-cost services, under their posting permissions,
   and the supplier's invoice is created at the ordered prices, matched and posted into the payable by the invoice
   service. The shipment never posts anything itself (A-187).

5. **Pricing: a Trader module that composes, never stores.** The trader's own screens that need several modules at
   once live in `Quicker.Trader`, an experience-tier module that depends on the others' contracts only and keeps no
   tables: the pricing board reads landed unit costs from Purchasing (`IPurchaseLandedCosts`), stock costs from
   Inventory, today's spot rate from Organization and the sales price list from Pricing (`IPriceLists`), and writes
   prices back through Pricing under its permissions. Its settings (target margin, rounding increment, the list) are
   ordinary organization settings under `trader.pricing.*`, so an admin sees them under Settings and the trader edits
   them in place (A-188). Later trader screens that compose modules (project health) follow the same pattern.

6. **Sales and money: the trader's words, the modules' documents.** A sale in one step is a Sales-module service
   (`QuickSaleService`) that makes the ordinary order, shipment and invoice through their own services and posts
   them, so stock, cost of sales, revenue, tax and the receivable are exactly what the full edition would record;
   nothing is kept when any step refuses. Payments are customer receipts applied to the invoice's open item. The
   Money screen is a reading of the receivables and payables aging with plain-language actions; it stores nothing
   and computes nothing the modules do not already provide (A-189).

7. **Nothing deferred to a second product.** The trader's later needs (shipments as a status timeline over the
   purchase order with landed costs and the exchange rate paid; pricing by purchase rate against a market rate with
   margins and loss alerts; plain-language sales, payments and aging with recommendations and charts; a project health
   page with break-even and a trader income statement; stock counting on the phone) are each a slice over the
   existing modules and are listed in the ROADMAP "Trader programme"; each adds its screens to the trader shell and,
   where it adds a field or a report, adds it for the full edition too.
8. **Project health is one read over the contracts, and its income statement is the ledger's.** `GET
   /api/v1/trader/health?companyId&from&to` (`reporting.report.run`; this month so far by default) is computed by
   the Trader module's `HealthService` from contracts the slice adds: the ledger's movement by account and month
   with closing entries left out (`ILedgerReader.MonthlyMovementAsync`), bank and cash balances at a date
   (`IBankAccountDirectory.BalancesAsync`), the open receivables and payables totalled (`IReceivables` and
   `IPayables.OpenTotalsAsync`), the shipments still on the way with their money (`IPurchaseShipments.UnderwayAsync`),
   the posted sales lines of a period (`ISalesLines.PostedAsync`), and the stock on hand per item it already had.
   The income statement groups the accounts by type and subtype exactly as the shipped profit and loss layout does
   (sales and contra revenue, cost of sales and variances, finance and exchange, income tax, every other expense
   account by its own name), so the trader's "what is left" is the accountant's net profit to the dinar, and the
   expense lines read as the chart names them (rent, salaries) rather than as a category of the program's own.
   Fixed costs are the trader's own monthly figure (`trader.health.fixed_costs_monthly`), else the average of the
   months with expenses among the last three complete ones, else unknown and said so; the margin that pays them is
   the period's gross margin on sales, else the pricing target turned into a margin on price; break-even sales a
   month, the month's pace (sales so far scaled to the month) against it, the cash runway (cash over fixed costs)
   and the money position (cash plus owed to you less you owe) follow. What to reorder takes the quantity sold in
   the last cover days (`trader.health.cover_days`, 90 by default) as what the next cover days need, less the stock
   and the goods on the way, in the item's unit. Warnings are codes with a value (a loss, fixed costs unknown, a
   short runway, break-even not covered at this pace, the margin under target, overdue receivables or payables,
   shipments needing attention, no recent sales, items to reorder); the screen words them, so the server stays
   language-free and the same codes can drive a notification later.
9. **Stock and counting on the phone reuse the scanner and the count, with one step added for a shop.** The Stock
   screen (`/trader/stock`) words the inventory module's own reads: what the store holds (`/inventory/stock`), what
   it is worth at cost (`/inventory/costing/valuation`), and the store's count. A count is three plain steps: start
   (a full count of the store created and frozen), count (on the phone with the camera through the existing scanner
   at `/m/count`, which already resolves barcodes, queues offline captures and syncs them; or typed on the screen
   against the frozen sheet), finish. Finishing is one new Inventory step, `POST /inventory/counts/{id}/finish`
   (`CountService.FinishAsync`, for the full edition too): the lines nobody counted are taken as unchanged when
   asked (counted = the snapshot plus what moved since the freeze, so they post nothing) and refused otherwise, as
   the review refuses them; every difference without a reason takes the reason given or the shipped `COUNT` reason
   ("Count difference", made on first use, applying to counts); then the ordinary review, approval and posting run,
   under their own permissions, so a shop's count lands in the books exactly as a warehouse's. The phone reaches the
   program over the local network: `QUICKER_LAN_HOST` in `.env` makes the web app call the API by the machine's
   address and lets the API accept that origin; the installable scanner (`/m`) then opens from the phone's home
   screen. A phone shell of its own (an iPhone build) stays deferred: the scanner is a web app the phone installs.

## Consequences

* A small business gets a program it can read, and the founder sells one product with two faces rather than two
  products; a trader who grows switches the edition and finds every record where it was.
* The trader screens are thin: they call the module services and own only wording, layout and the recommendation's
  presentation, so a fix in a module fixes both editions.
* The edition reaches a browser at the switch (the session's tenant is updated) and other members at their next
  sign-in or token refresh; a stale token shows the old menus for at most the access-token lifetime.
* Deferred (named in `docs/PROGRESS.md`): a home page of the trader's own (the full home shows for now); the
  "order from this supplier" step orders every line from one supplier, a split order is a suggestion the trader
  carries out by hand; offers in a foreign currency need a spot rate of the day entered under Rates, else the
  comparison says so; costs that arrive after the goods were received (a landed-cost document by hand); partial
  arrivals (one receipt per shipment); paying the supplier from the shipment screen; prices per customer group or
  quantity break and margins per item or category on the pricing board; a trader-worded customer page, discounts
  and returns from the sales screen, receipts on account; profit by item (needs the cost of each sale from the stock
  ledger; the health screen shows sales by item and the pricing board the margin per item today); a cash forecast by
  week from the open items' due dates; a lead time per supplier in the reorder (one cover-days setting for now);
  recording an expense from the health screen (the Expenses screens stand); a phone shell of its own (the scanner is
  the installed web app; a store-listed iPhone build is a later programme); printing barcode labels for items that
  carry none (the item code is typed on the phone meanwhile); a trader-worded mobile home (the scanner's own words
  stand: Count, Transfer, Receive, Pick, Queue).
