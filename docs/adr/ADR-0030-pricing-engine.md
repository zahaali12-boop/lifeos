# ADR-0030: Pricing engine determinism and explanation

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Price lists by customer, group, currency, date range and quantity breaks; line and document discounts; promotions and bundles; deterministic and explainable results even when several rules and a foreign currency apply to one line (hard scenario 14). Purchasing uses the same machinery for supplier prices.

## Decision

### Model

* `prc_price_lists`: type (sales | purchase), currency, tax inclusivity, validity, parent list (for derived lists: parent ± percentage, rounded by rule), scope (all | customer group | specific customers), priority.
* `prc_price_list_items`: item or variant, UoM, minimum quantity (quantity break), price, validity.
* `prc_customer_price_agreements`: customer, item/variant/category, UoM, min quantity, price or discount, validity (highest specificity).
* `prc_discount_rules`: level (line | document), scope (item, category, brand, customer, group, channel, payment terms), condition (min quantity, min amount, validity, weekday), value (percentage | amount | fixed price), combination policy (`exclusive` | `stackable`), priority.
* `prc_promotions`: buy X get Y (same or other item, free or discounted), bundle pricing (kit price when all components present), volume tiers across a category, validity, usage limits, coupon codes; each with explicit priority and combination policy.
* `prc_price_floors` (minimum margin or price by item/category) whose breach raises a workflow block.

### The deterministic pipeline (per line, then per document)

1. **Base price resolution** (first match wins, in this order): manual override on the line (permission-gated, audited) → customer price agreement → price list explicitly on the document → customer's default price list → customer group price list → company default price list → item's list price. Within the winning source, the quantity break is the highest minimum quantity ≤ line quantity in the line's UoM (with UoM conversion of breaks when needed).
2. **Currency**: if the price source currency differs from the document currency, convert at the document's rate type and date (rate captured), then round to the document currency's price precision.
3. **Line discounts**: collect matching rules; apply per combination policy: `exclusive` rules compete and the best for the customer wins; `stackable` rules apply sequentially in priority order on the running net. Ties break by (priority, most specific scope, earliest valid_from, lowest id).
4. **Promotions**: evaluated after line discounts on the document's lines as a set (bundles and buy-X-get-Y need the whole basket); free goods appear as explicit lines with zero price and a link to the promotion; usage limits are checked under a lock at confirmation.
5. **Document discount**: percentage or amount, allocated to lines by largest remainder for revenue and tax correctness.
6. **Tax**: through the tax engine (inclusive/exclusive handling per ADR-0018).
7. **Rounding**: per ADR-0005.
8. **Floors and margin checks**: compare net price against floors and expected cost; breaches raise blocks routed to approval (ADR-0020).

### Explanation

Every line stores `price_breakdown jsonb`: the ordered list of steps with the candidate rules considered, the winner and why (rule id, scope, quantity break, rate used, amounts before/after), plus the final net, tax and gross. The UI shows it as the "why this price" panel; the API returns it; reports can aggregate discount effects by rule. The breakdown is frozen at posting; re-pricing a draft regenerates it.

### Determinism guarantees

* Pure function of (document header, line, rule set as of the pricing date, rates as of the pricing date); a `pricing_date` on the document (default document date) fixes validity evaluation, so re-pricing a quote tomorrow gives the same answer unless the user changes the date.
* Property tests: same inputs → same breakdown; reordering rule insertion never changes results; stacking is order-independent given priorities.

## Alternatives considered

* **"Best price wins" only.** Simple but cannot express stackable trade and promotional discounts that customers expect in distribution.
* **Scripted pricing.** Unbounded and unexplainable; the rule model covers what NetSuite, Dynamics and Odoo offer and stays auditable.

## Consequences

* Sales reps and customers get a printable explanation for every price; disputes are resolved from data.
* Purchasing reuses the same engine with supplier price lists and agreements, so price history and comparison are consistent.

## Amendment 2026-09-25 (slice 5.2, A-144)

* **Ties never break by id.** The last tie-break of competing rules is the rule's code (unique per company), not the lowest id: ids are time-ordered, so "lowest id" would make the result depend on the order rules were created, which the determinism guarantee forbids. Price-list entries and agreements cannot tie at all: their natural keys (item, variant, unit, break, start) are unique in the database.
* **Where pricing lives.** Pricing is a core module of its own (`Quicker.Pricing`), so sales and purchasing both call it and neither owns it. Price lists are assigned to customers and groups by `prc_price_list_assignments` (the "scope" of the model above) rather than by a column on the customer account, so the partner module never depends on pricing.
* **Precision.** Unit prices keep two decimals more than the currency's minor unit; amounts round to the minor unit at every step; list rounding rules round to a multiple of an increment through `RoundingPolicy.RoundToMultiple`.
* **Promotions.** Exclusive promotions compete greedily on the basket (greatest benefit first, re-evaluated on the lines still free); stackable ones follow on the lines no exclusive promotion took. Goods given away are their own line at the regular price with the promotion's discount.
* **Steps 6 and 8 until the tax engine.** Until 5.3 supplies line tax rates, a price on the other tax basis than the document's is refused (`pricing.tax_basis_mismatch`) rather than converted, and floors compare on the price's own basis. Floor breaches are reported by the engine; routing a block to approval belongs to the sales documents (5.4).

## Amendment 2026-09-25 (slice 5.3b, A-148)

* **Tax basis.** Step 6 now converts a price on the other tax basis at the item's sales tax rate for the customer on the pricing date, which the pricing service reads from the tax engine before calling the pure engine; only an item the tax engine cannot determine is still refused.

