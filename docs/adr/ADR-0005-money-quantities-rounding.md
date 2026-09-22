# ADR-0005: Money, quantities, decimals and rounding

Status: proposed · Date: 2026-09-22

## Context

Books must balance to the smallest currency unit across 80+ currencies with 0, 2 or 3 decimal minor units (IQD in practice 0, KWD/BHD/OMR 3), unit conversions must not drift over thousands of transactions (hard scenario 9), tax must round the way each regime expects, and every rounding must be explicit and centralised.

## Decision

### Types (in `Quicker.Kernel`)

* `Money { decimal Amount; Currency Currency }`: addition/subtraction only between equal currencies (compile-time via operator overloads that take `Money`, runtime check on currency), multiplication by `decimal`, no implicit conversion to/from `decimal`. `Money` never rounds itself; `RoundingPolicy.Round(money, context)` returns a rounded `Money` and the remainder.
* `Quantity { decimal Value; UnitOfMeasure Unit }` with `Convert(to, conversion)` returning an exact result when the conversion factor is rational, otherwise rounding to the target unit's precision through the same policy.
* `ExchangeRate { Currency From; Currency To; decimal Rate; RateType Type; DateOnly Date }` with `Convert(Money)`; inverse rates are computed, never stored rounded.
* `RoundingPolicy`: mode (`HalfAwayFromZero` default, `HalfEven` optional), currency minor units, optional cash rounding increment (for example 250 IQD, 0.05 CHF) applied only to cash totals, and the allocation algorithm for distributing a rounded total across lines (largest remainder).

### Storage precision (PostgreSQL `numeric`)

| Kind | Type | Notes |
|------|------|-------|
| Monetary amount (line and document amounts, journal amounts) | `numeric(20,6)` | stored at full precision; **posted journal amounts are rounded to the currency's minor unit** before insert; a check constraint enforces scale ≤ minor units on `journal_lines` and `stock_value_entries`. |
| Unit price, unit cost | `numeric(24,10)` | never rounded until multiplied into an amount. |
| Quantity | `numeric(24,9)` | stored in the item's base unit; document UoM quantity stored alongside. |
| UoM conversion factor | `numerator numeric(24,9)`, `denominator numeric(24,9)` | rational factor, so carton→piece→dozen is exact. |
| Exchange rate | `numeric(24,12)` | with explicit `quote_direction`. |
| Percentages (tax, discount) | `numeric(9,6)` | 12.5% stored as 12.500000. |

### Rounding rules

1. **Line first, then document** by default: each line's net, tax and gross are rounded to the currency minor unit; document totals are sums of rounded lines. Regimes or companies may switch to **document-level tax rounding** where tax is computed on the summed base and the difference to the sum of line taxes is allocated by largest remainder. Both are recorded on the document (`tax_rounding_mode`).
2. **Currency conversion** happens per journal line (transaction → functional → reporting); the functional and reporting totals of an entry are then balanced by a rounding line to the `RoundingDifferences` account (typically ±1 minor unit), created by the posting engine, never by modules.
3. **Tax-inclusive prices**: net = round(gross / (1 + rate)), tax = gross − net, so the printed gross is exact.
4. **Cost per unit** is kept unrounded in value entries (`unit_cost numeric(24,10)`), and only `cost_amount` (unit cost × quantity) is rounded to the minor unit; the remainder is carried in the layer so that consuming a whole layer sums exactly to the layer's cost (no residual value left on a zero-quantity layer).
5. **Allocations** (landed cost by value/weight/volume/quantity, document discounts, payment allocation, elimination percentages) use largest-remainder distribution so the parts always sum to the whole.
6. Display formatting is a presentation concern (locale digits, separators) and never feeds back into calculations.

### Enforcement

* Analyzer forbids `double`/`float` and `Math.Round` in domain and application projects; only `RoundingPolicy` may round.
* Property-based tests (FsCheck): `sum(allocate(total)) == total`, `convert(convert(q, a→b), b→a) == q` for rational factors, and `Money` arithmetic laws.
* A migration test asserts no `float`/`double precision`/`real` column exists in `app`, `reporting` or `control`.

## Alternatives considered

* **Integer minor units (cents).** Exact and fast, but awkward for 3-decimal currencies, unit costs with more precision than the minor unit, and rates; `decimal` gives the same exactness with less ceremony.
* **Rounding at display time only.** Rejected: the ledger must hold the amounts that legally exist on the documents, to the minor unit.

## Consequences

* Every rounding is a call to one class, with the context (currency, mode, increment) available in tests and in the "why" breakdowns.
* Schema and analyzer rules make the wrong thing hard to do, which matters when many sessions write code.
