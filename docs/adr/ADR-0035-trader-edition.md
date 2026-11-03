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
10. **The trader's home is the day in plain words, computed nowhere.** When the edition is `trader`, `/` shows the
   trader's home instead of the full dashboard: what was sold today (the home read model), whether the month pays,
    the cash and how long it lasts, what is owed each way, the shipments on the way (all from `GET /trader/health`),
    "what to do today" (the health warnings, each worded as on Health and linking to the screen that acts on it),
    the four things a trader does most (sell, compare offers, count the store, set the prices) and sales by month.
    The page owns no figure of its own, so it never disagrees with Health or Money; a workspace switched back to the
    full edition gets the full home again.

11. **Paying for a shipment moves the books (T8, A-193, A-194).** "Paid for the goods" on a shipment is no longer a
    note: `POST /purchasing/shipments/{id}/pay` pays the supplier from a bank account or cash box through the Banking
    payment service, as a supplier advance before the invoice exists and as a payment of the invoice's open item
    after it; a payment in a foreign currency from a dinar account carries the dinars that left the account, which
    give the effective rate. Each payment is kept on the shipment (`pur_shipment_payments`) with what it applied.
    Recording the supplier's invoice applies the advances to it through the payables settlement service, the
    difference between the advance's rate and the invoice's posted as realised exchange. A journey cost names the
    party it is paid to (the forwarder, customs); `POST /purchasing/shipments/{id}/costs/{costId}/pay` pays it the
    same way, and arrival turns each party's estimated costs into its invoice of landed-cost charges, which clears
    the landed-cost clearing account and to which the advance is applied. Money gets a "Pay" for each supplier that
    pays the oldest invoices first through the same payment service. A bank or cash line of a journal (the setup
    wizard's opening balances, a manual or recurring journal) now records the movement in the box it names, so a
    box's balance on every screen equals its ledger account (A-194). The whole chain, from the opening cash through
    offers, order, payments, arrival, invoice, pricing, sale, receipt and count to the health figures, is one test
    whose every figure ties to the ledger and passes the invariant harness.

12. **Expenses and the owner's own money, in one step each (T9, A-195).** The trader's Expenses screen records what
    the shop spends through the Expenses module's own services (`POST /expenses/quick`): paid from a cash box it is a
    petty cash voucher from the fund that sits on the box (made on first use, with the box's code and name), paid from
    a bank account it is the payee's bill confirmed and paid the same day, not paid it is the payee's bill left owed,
    which Money then pays. The usual kinds of spend (`POST /expenses/categories/defaults`) are made once on their usual
    accounts. The owner's capital and drawings are manual journals posted through a new Accounting contract
    (`IJournals`: a module's journal created and posted in one go and read back by its reference): money put in is
    Dr the box / Cr the capital account, money taken out Dr the drawings account / Cr the box, the box named on the
    bank line so its balance follows (A-194); the equity accounts are settings with the chart's usual codes as
    defaults. The Trader module keeps no table for them: the journals are the record, numbered, listed and reversible
    in the full edition.

13. **The founder's first day: what a real trader asked for (T10).** The field feedback of 2026-11-03 is taken as
    it came, in order of harm. The web app renews its access token silently (a ten-minute token and a session of
    days had been read as a sign-out after ten minutes and an "Unauthorized" on the next click). A sale in one step
    refuses what the store cannot cover in the trader's words, before anything is written. The workspace name leaves
    the top bar. The Offers screen is rebuilt around how offers are collected in practice: a supplier not in the list
    is made from the comparison itself (the quick create, with the next code), an item not found is made on the spot
    the same way, the description grows as it is typed, the unit is not asked (the item's own unit; pieces for a line
    without an item), a supplier is added to a comparison after it was made, the first supplier's offer form opens
    with the comparison and each saved offer opens the next supplier's, the comparison refreshing with every offer,
    and a comparison that led to no order is deleted for good (`DELETE /purchasing/rfqs/{id}`; one that became an
    order is kept).

14. **The day's rate (T10c).** A trader who buys in dollars and sells in dinars lives by the morning's rate. From the
    hour the trader chose (`trader.rates.prompt_hour`, 08:00 by default; a negative value never asks), the first open
    of the app on a day with no spot rate entered for that day asks for it: one box per currency the shop buys in
    (`trader.rates.currencies`, plus those seen on shipments; the dollar when nothing is named or seen), the last
    known rate as a hint, saved in one go through `POST /trader/rates/today` into the organization's rates (the new
    `IExchangeRateEntry` contract beside the resolver), so every document of the day converts at it and the pricing
    board re-values at it. "Later" keeps quiet until the app is opened again; a rate of the day already entered is
    corrected only with a reason, as under Exchange rates (ADR-0017). The hour is changed from the prompt itself,
    which is where the trader meets it.

15. **The pricing board edited by hand (T10d).** The board had priced only what a shipment or the stock ledger
    could cost, and only suggested. The trader prices anything now: "New pricing" names an item (made on the spot
    when new), its selling price and, when the program knows no cost yet, what a unit costs, kept as the item's
    standard cost for the company through the items module and used as the basis until a shipment or the ledger
    knows better; "cost unknown" on a row is a button that asks for that cost in place. Under every price box the
    floor is shown, the cost basis rounded up to the increment, and a price typed below it says what a unit would
    lose. A price is taken off the list from today (`DELETE /trader/pricing/prices/{itemId}` over the new
    `IPriceLists.RemovePriceAsync`: an entry that began earlier ends the day before, one that began today is
    deleted), so the list stays the trader's to shape. The unit a price is for stays the item's sales unit; changing
    units stays on the item.

16. **The next step follows the record (T10e).** The founder wanted a customer or supplier just made to lead on to
    the sale or the offers, and every screen to say what comes next. In the trader edition the top bar's "New" lists
    the day's few starts (a sale, an offer comparison, an expense, a customer, a supplier, an item), each landing on
    the trader's screen with its form open; a customer saved on Customers goes on to Sales with the sale open and
    the customer chosen (`/trader/sales?customer=`), a supplier saved on Suppliers goes on to Offers with a comparison
    open and the supplier ticked (`/trader/offers?supplier=`), and a customer not in the list is made from the sale
    form itself. After a sale the screen offers the payment of that invoice and the same customer again; after a
    payment, what is owed on Money and another sale; after goods are received, the selling prices; after prices are
    applied, a sale at them. The full edition keeps its own next steps (the customer's 360, the supplier's record).

17. **The founder's second day: the blockers (T11a).** Five things stood between the founder and the day's work.
    (1) The app open in two windows signed him out: one window rotated the refresh token, the other presented it
    minutes later, and the replay detector revoked the family. The web app now follows the session another window
    stored (the storage event, a re-read before renewing), and the API honours a token rotated within the last 30
    seconds with a session of its own (ADR-0014, amended). (2) A sale of an item with no tax group was refused under
    a registered regime; the trader wants neither tax nor categories, so the company setting `tax.unclassified_items`
    (`refuse`, the full edition's default, or `untaxed`) is written as `untaxed` by the switch to the trader edition,
    and such a line carries no tax, reason `unclassified`. (3) Items shows what the store holds of each, all
    warehouses of the company together. (4) The choice of offer is the trader's: a supplier that priced only some
    lines is ordered from for those, the rest named in the audit and asked about on the screen first; the
    recommendation stays a suggestion. (5) A price typed on the board shows its margin as it is typed and saves from
    its own row.

18. **The money's own screen, newest first, and the period (T11b).** The founder asked to see each cash box and
    bank account on its own, what it holds, what came in and went out and the balance after every movement; every
    list newest first whatever else; and filters by day, month, quarter and any range. Cash and bank
    (`GET /trader/cash`) reads the bank subledger through a new contract method (`IBankTransactions.MovementsAsync`):
    per account the balance now, the balance the day before the period began, what came in and went out, the
    balance at the period's end, and the movements newest first with the balance after each (the closing balance
    less the movements above it). Lists are ordered by the document's own date and then by creation, newest first
    (comparisons and shipments by the day they were made, in the company's time zone; invoices by their date), and
    take `from` and `to`. One period chooser (today, the last seven days, this month, last month, this quarter, this
    year, everything, or typed dates) sits on Sales, Offers, Shipments, Expenses and Cash and bank.
19. **The trader program on its own, with nothing in it, and on the phone (T11c; A-202).** The founder's trader
    program runs as a second installation of the same product next to the full system on one machine, sharing nothing
    with it: `docker-compose.trader.yml` (Compose project `quicker-trader`) has its own PostgreSQL, file store, mail
    catcher, API, worker and web app, its own volumes (named by the project) and its own port (the app on 5174, the
    API behind it and not published; the full system keeps 5173 and 8080), reading the same `.env` for the machine's address on the local
    network. Its migrator runs `prepare`, a new verb that creates the database, applies the migrations and the
    reference data, and never seeds the demo workspace, so the installation starts with no data at all. The web app of
    that stack is built with `VITE_EDITION=trader`: the sign-up screen says so and sends `edition: trader`, and
    `POST /auth/signup` takes an `edition` (`full` by default; an unknown one is refused with `tenant.edition_invalid`
    before anything is made), so the workspace opens as the trader program from its first screen without a visit to
    Settings. What an edition means is applied by the server, at sign-up and at every switch: `ITenantEditionStep`
    (Tenancy contracts) is run by `AccountService.ChangeEditionAsync` after the edition is recorded, and the tax
    module's step writes `tax.unclassified_items` organization-wide (untaxed for the trader, refuse for the full
    edition), which the settings screen used to do from the browser (§17). On Windows, `deploy\windows\setup-trader.bat`
    installs the stack once and registers "Quicker Trader auto-update" (`update-trader.bat`; `register-task.ps1` now
    takes a task name and a script); the two stacks share one `update.lock` so only one build runs at a time on the
    machine. On the phone the app is a home-screen app: a web manifest (standalone display, icons at 192 and 512 and
    the Apple one at 180, rendered from the app's icon), the Apple meta tags, and a service worker that caches nothing
    (an update is live at the next open) and exists only so that browsers offer the install. Over plain HTTP on the
    local network, which is how the founder's machine serves it, Safari's "Add to Home Screen" opens the app
    full-screen with its own icon and Chrome on Android adds the same shortcut; the browser's install prompt and
    working offline need HTTPS, and a store listing needs a native wrapper (a Mac with Xcode and an Apple developer
    account): both are deferred, stated as such to the founder. The stack's web app is the built app behind nginx
    (the `static` target of `web.Dockerfile`, `VITE_EDITION` a build argument), not the development server the full
    system's Compose file still runs: a tenth of the memory on the founder's 8 GB machine, and nginx
    (`deploy/docker/nginx.conf`) proxies `/api` and `/health` to the api container, so the app calls the API on its
    own origin, nothing about the API's address is baked in, a phone on the local network reaches app and API through
    the one port, and no cross-origin rule applies. The manifest is the app's own (`public/manifest.webmanifest`,
    opening on `/`, linked from `index.html` in every mode); the PWA plugin generates only the service worker, in
    production builds, as it has since the scanner (3.8), whose generated manifest that opened on `/m` it replaces.

## Consequences

* A small business gets a program it can read, and the founder sells one product with two faces rather than two
  products; a trader who grows switches the edition and finds every record where it was.
* The trader screens are thin: they call the module services and own only wording, layout and the recommendation's
  presentation, so a fix in a module fixes both editions.
* The edition reaches a browser at the switch (the session's tenant is updated) and other members at their next
  sign-in or token refresh; a stale token shows the old menus for at most the access-token lifetime.
* Deferred (named in `docs/PROGRESS.md`): the
  "order from this supplier" step orders every line from one supplier, a split order is a suggestion the trader
  carries out by hand; offers in a foreign currency need a spot rate of the day entered under Rates, else the
  comparison says so; costs that arrive after the goods were received (a landed-cost document by hand); partial
  arrivals (one receipt per shipment); inventory valued at the advance's rate for the part paid in advance (IFRIC 22;
  the stock is valued at the receipt's rate and the difference is realised exchange, A-193); a supplier invoice that
  differs from the order (the invoice is created at the ordered prices); paying a cost after it was invoiced from the
  shipment (Money pays it); prices per customer group or
  quantity break and margins per item or category on the pricing board; a trader-worded customer page, discounts
  and returns from the sales screen, receipts on account; profit by item (needs the cost of each sale from the stock
  ledger; the health screen shows sales by item and the pricing board the margin per item today); a cash forecast by
  week from the open items' due dates; a lead time per supplier in the reorder (one cover-days setting for now);
  a payee made from the expense form (the suppliers screen makes one); an expense in a foreign currency from the trader's screen (the full Expenses screen takes it); reversing an owner movement from the trader's screen (the Journals screen reverses it); a phone shell of its own (the scanner is
  the installed web app; a store-listed iPhone build is a later programme); printing barcode labels for items that
  carry none (the item code is typed on the phone meanwhile); a trader-worded mobile home (the scanner's own words
  stand: Count, Transfer, Receive, Pick, Queue).
