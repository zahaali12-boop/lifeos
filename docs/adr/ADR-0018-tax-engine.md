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
