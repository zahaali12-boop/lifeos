# ADR-0018: Tax engine and e-invoicing adapters

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

VAT/GST/sales tax, inclusive and exclusive pricing, withholding tax, exemptions, reverse charge, tax reports, pluggable e-invoicing adapters (ZATCA, Peppol, CFDI later); many regimes across the Middle East with different rounding, tax points and reporting; Iraq has no general VAT but selective sales taxes and withholding.

## Decision

### Model

* `tax_regimes` per country/jurisdiction: name, tax type family, rounding mode (line | document), tax point rule (invoice date | payment date for cash-basis schemes | delivery), reporting periods, e-invoicing scheme.
* `tax_codes`: code, regime, kind (`vat`, `gst`, `sales_tax`, `withholding`, `excise`), `rates` (effective-dated `tax_rates` rows), `is_recoverable`, `is_reverse_charge`, `is_exempt` with `exemption_reason_code` (ZATCA/Peppol codes), `applies_to` (goods | services | both), input/output account roles, reporting box mapping.
* `tax_groups`: **item tax groups** (standard goods, zero-rated, exempt, services, excise) and **partner tax groups** (domestic registered, domestic unregistered, GCC, export, government, non-resident).
* `tax_determination_rules`: (regime, item tax group, partner tax group, ship-from country/region, ship-to country/region, document type, valid dates) → tax code, with most-specific-wins ordering; admins edit it as a matrix. Explicit tax code on a line always overrides with an audit note.
* `tax_lines`: per document line: code, rate, base (tc/fc), amount (tc/fc), inclusive flag, reverse charge flag, withholding flag, box mapping; the document stores the rounding mode used. Journal lines carry `tax_code_id` and `tax_base_amount` so tax reports reconcile to the GL.
* **Inclusive/exclusive**: price lists declare inclusivity; the pricing engine produces net, tax and gross per line using ADR-0005 rules.
* **Withholding tax**: codes with `withhold_at = invoice | payment`, thresholds, and certificates; withheld amounts post to WHT payable/receivable and reduce the settlement; certificate reports per supplier/customer and period.
* **Reverse charge**: output and input tax lines posted simultaneously (net zero cash effect) with both boxes reported.
* **Exemptions**: partner certificates with validity (`tax_exemptions`) checked by the determination rules; the exemption reason prints on the invoice.
* **Tax periods and returns**: `tax_return_periods` with status; a return is a report computed from `tax_lines`/journal lines with drill-down; locking a return period blocks changes to tax lines in it (corrections go to the next period with reference).
* **Registration numbers** per company and branch (some regimes register branches) and per partner.

### E-invoicing adapters

`ITaxClearanceProvider` with operations `Validate`, `Sign`, `Submit`, `Cancel`, `GetStatus`; documents record `einvoice_submissions` (provider, uuid, hash, previous hash for chains such as ZATCA, QR payload, XML/JSON artefact in object storage, status, response). Adapters: **ZATCA Phase 2** (UBL 2.1, XAdES signature, invoice hash chain, clearance for standard invoices, reporting for simplified invoices, QR code with TLV), **Peppol BIS Billing 3.0** via an access point, **Egypt ETA** and **CFDI** as later adapters. The generic UBL 2.1 export is available to every tenant.

Invoices in regimes with clearance cannot be printed as tax invoices until cleared; the print template shows the status and QR.

### Seeded templates

Iraq (selective sales tax codes, contractor withholding), Saudi Arabia, UAE, Bahrain, Oman, Qatar, Kuwait, Jordan, Egypt, Turkey and a generic EU VAT set (ASSUMPTIONS A-004). Each template is a data file, versioned, with a validation date.

## Alternatives considered

* **External tax service (Avalara-style).** No coverage for Iraq and thin coverage for the region; the matrix model is what regional ERPs do and what accountants expect to configure.
* **Tax logic in pricing only.** Tax must also apply to journals, expense claims, fixed assets and reverse-charge purchases; a central engine serves all.

## Consequences

* New regimes are data plus, where clearance is required, an adapter.
* Tax reporting reconciles to the ledger by construction.

## Amendment 2026-09-25 (slice 5.3, A-146)

* **A tax ledger instead of `tax_lines`.** Each document keeps its lines' tax on its own lines (sales and purchasing tables), as it keeps their prices; the tax module keeps `tax_entries`, an append-only ledger that documents write as they post (one row per line and code, in transaction and functional currency, with the journal entry), through `ITaxLedger` in the posting transaction. The return is computed from the ledger and reconciled to the movement on the tax accounts of the general ledger. A write dated in a filed return period is refused; a reversal writes the opposite rows on its own date.
* **Treatment instead of `is_exempt`.** A code is standard, zero-rated, exempt (with the reason printed) or out of scope, because zero-rated and exempt supplies go in different boxes and only exempt ones make input tax irrecoverable. Each code names the boxes of its base and tax on the sales and on the purchase side of the return, so a reverse charge reports in both.
* **The matrix is by direction.** Determination rows are for sales or for purchases rather than by document type: no template needs more, and every sales document then taxes a line alike. The most specific row wins by fixed weights (item group 8, partner group 4, ship-from 2, ship-to 1), then the latest start; rows are unique per combination and start, so there is no priority to set and no tie. A customer's exemption certificate relieves the tax a line would otherwise carry; a line the matrix already puts at zero keeps its code.
* **Registration decides.** A company charges and recovers tax only in a regime it is registered in; unregistered, its lines carry no code, which the calculation says.
* **Rounding.** Per line unless the regime or the company asks for per document (ADR-0005); the document level used travels with the calculated document.
* **Templates reuse groups.** Installing a country reuses the tenant's item and partner tax groups by code, so companies in several countries share one set of item groups.

