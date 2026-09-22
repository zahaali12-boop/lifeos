# ADR-0017: Multi-currency: three amounts per line, rate types, realized and unrealized FX

Status: proposed · Date: 2026-09-22

## Context

Transaction, functional and reporting currencies; dated exchange rates; realized FX on settlement; month-end revaluation of open items with automatic reversal (hard scenario 3); Iraq's dual official/market rates; 80+ currencies with different minor units; intercompany in different currencies (scenario 8).

## Decision

### Currencies and rates

* `org_currencies` seeded from ISO 4217 (code, numeric code, minor units, symbol, names EN/AR); company overrides for display precision and cash rounding (ADR-0005).
* Each company has a **functional currency** (immutable after the first posting) and an optional **reporting currency** (group currency); the tenant may define a second reporting currency for consolidation.
* `org_exchange_rate_types`: `spot` (default for transactions), `closing` (period-end revaluation), `average` (P&L translation in consolidation), `budget`, plus admin-defined (`official`, `market` for Iraq). Each company sets which type each process uses.
* `org_exchange_rates`: `(tenant, rate_type, from_currency, to_currency, valid_from date, rate numeric(24,12), source, entered_by)`, effective-dated with exclusion constraints; the rate for a date is the latest `valid_from ≤ date`. Rates are stored as `1 from = rate to`; the engine derives the inverse and cross rates through the functional currency (no triangulation surprises: cross rates always go through the company's functional currency unless a direct rate exists).
* Rate providers (ECB, Open Exchange Rates, Central Bank of Iraq page scraper as an adapter) import into the same table with `source`; manual entries and per-document overrides are audited with a reason.

### Journal lines carry three amounts

`amount_tc` (transaction currency), `amount_fc` (functional), `amount_rc` (reporting), with `currency_tc`, `rate_tc_fc`, `rate_fc_rc`, `rate_type`, `rate_date`. Balancing is checked in all three; rounding differences go to the rounding account (ADR-0005). Reports in reporting currency use the stored `amount_rc`, never a recomputation, so history does not move when rates change.

### Realized FX on settlement

An open item is carried at its **booked functional value** (the rate on its posting date, adjusted by any permanent revaluation policy). A settlement (receipt, payment, credit application, netting) converts the settled transaction amount at the **settlement rate** (payment date, or the bank's actual rate when the bank account is in a third currency, entered on the payment). The difference between the settled portion's booked functional value and its settlement functional value posts to realized FX gain or loss on the settlement row and in the GL (see POSTING_RULES). Partial settlements realize proportionally; bank fees deducted post as expense in the same journal (scenario 3).

Three-currency settlements (EUR invoice paid from a USD bank in a GBP-functional company) are handled by converting both sides to functional currency and posting the difference; the bank line uses the bank account's currency amount.

### Unrealized FX (revaluation)

* `close_fx_revaluation_runs` per company, period and scope (AR, AP, bank/cash, intercompany, other monetary accounts flagged `revalue`). For each open item (or account balance in a foreign currency) the run computes the functional value at the **closing rate** and posts the difference to unrealized gain/loss against the control account (with the open item as subledger reference, so control still equals subledger).
* **Open items (AR, AP, IC)**: the revaluation journal is flagged `auto_reverse` with reversal date = first day of the next period, so the next period starts from booked values and settlement realizes the full difference. This is the brief's requirement and the common practice in Dynamics and NetSuite.
* **Bank and cash balances**: the translation is permanent (no reversal) because the carrying amount of cash in foreign currency is its closing-rate value (IAS 21.23); the booked functional value of the bank account is updated for subsequent realized calculations. Configurable per company (`bank_revaluation_mode = permanent | reversing`).
* Runs are repeatable within a period (a rerun reverses its predecessor first) and locked once the period is hard-closed.

### Reporting currency and consolidation

* Reporting-currency amounts on lines use the transaction date spot rate. Consolidation (ADR in M6) translates subsidiaries with closing rate for balance sheet, average rate for P&L, historical for equity, posting the difference to a currency translation adjustment in equity (IAS 21.39).

### Intercompany in different currencies

The originating company posts in its functional currency; the mirrored document in the counterpart company is in the transaction currency converted at that company's rate for the same date and rate type; differences appear only at settlement as realized FX in each company and are eliminated in consolidation at the group level as FX on intercompany balances (scenario 8).

## Alternatives considered

* **Storing only transaction and functional amounts, computing reporting on the fly.** Cheaper storage but history changes when rates are corrected; the third column costs little and keeps reports stable.
* **Revaluing bank balances with reversal.** Simpler uniformity, but wrong under IAS 21 for cash; offered as an option for companies whose auditors prefer it.

## Consequences

* Every line explains its rates; FX gains and losses are traceable to the settlement or run that produced them.
* Iraq's dual-rate reality is data (rate types), not code.
