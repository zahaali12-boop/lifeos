# ADR-0031: Settlement lines carry their booked functional value

Status: accepted · Date: 2026-09-23

## Context

A journal entry is posted in one transaction currency at one rate (ADR-0017): every line's functional value is the transaction amount converted at the entry's rate, and rounding differences go to a rounding line. Settlements break that rule on purpose. When a EUR invoice booked at 1.10 is paid at 1.08, the payable must leave the books at the value it was booked at (its open item's remaining functional amount, to the fils), the bank must move by what the bank actually took, and the difference between the two is a realised exchange gain or loss (POSTING_RULES §4 "Supplier payment", hard scenario 3). Credit applications, advance applications and payments all need it; receivables (M5) and bank transfers (M6) will need the same.

## Decision

1. `PostingLine` gains an optional `AmountFc`: the line's functional-currency value, fixed by the caller. When it is given, the engine uses it instead of converting the transaction amount; the reporting value follows the functional one. A line may carry a functional amount and no transaction amount: that is the realised exchange difference, and it goes to `FxGainRealized` / `FxLossRealized`. The engine still requires the transaction amounts to balance, still appends its rounding line when the functional or reporting totals are off by rounding, and rejects a fixed amount on the other side of the transaction amount, or one that differs from it when the company's currency is the transaction currency.
2. Journal lines may therefore carry a functional amount and no transaction amount without being rounding lines (the schema check on `gl_journal_lines` allows either amount). The "entries balanced" invariant is unchanged: transaction, functional and reporting totals each balance.
3. Who may fix a functional value: only subledger settlements and bank movements, through the modules that own the subledger items (Payables, Receivables, Banking). Manual journals never set it; they touch control accounts only by naming the subledger item they adjust (POSTING_RULES §8).
4. Each settlement records what it fixed: the amount in the item's currency, the functional value relieved on the settled item (its booked rate), the functional value of the settling item or bank movement (the settlement rate), and the resulting exchange difference, together with any discount, withholding, charge or write-off it carried. The subledger reconciliation rule (DOMAIN_MODEL §13.1) holds because the settled item's remaining functional amount moves by exactly the functional value posted against it.

## Consequences

* Realised FX is computed by the module that knows both values, never estimated by the engine from rates.
* Reversal of a settlement mirrors the journal (all three currencies copied), so an item reopened after a reversal is back at its booked value.
* The GL inquiry shows such lines with a functional amount and no transaction amount; the report layer labels them as exchange differences.
