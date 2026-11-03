using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Organization.Contracts;
using Quicker.Printing.Contracts;

namespace Quicker.Sales.Application;

/// <summary>
/// What a sales invoice or credit note exposes to its print templates (roadmap 5.9, ADR-0022's data contract): the
/// document's dates, terms and references, each line with its price, discount and tax, the tax summary per code, the
/// payment schedule from the receivable items, the totals and the amount in words in both languages, and the
/// e-invoice QR payload. A draft prints only as the pro forma variant: a tax invoice is a posted document.
/// </summary>
public sealed class SalesInvoicePrintModel(InvoiceService invoices, ICompanyDirectory companies, ICurrentPrincipal principal) : IPrintModelProvider
{
    public string DocumentType => InvoiceService.DocumentType;

    public IReadOnlyList<PrintVariant> Variants { get; } =
    [
        new(PrintVariants.Standard, LocalizedText.Bilingual("Tax invoice", "فاتورة ضريبية")),
        new(PrintVariants.Proforma, LocalizedText.Bilingual("Pro forma", "فاتورة أولية")),
        new(PrintVariants.Brand, LocalizedText.Bilingual("Branded invoice", "فاتورة بهوية الشركة")),
    ];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (principal.Principal is { } who && !who.Has(SalesPermissions.InvoiceRead))
        {
            return Error.Forbidden("print.forbidden", "Reading sales invoices is not permitted.");
        }

        if (subject.DocumentId is not { } id)
        {
            return Error.Validation(PrintErrors.ParameterRequired, "The invoice to print is named by its id.");
        }

        var found = await invoices.GetAsync(id, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var i = found.Value;
        var proforma = string.Equals(variant, PrintVariants.Proforma, StringComparison.Ordinal);
        if (i.Status == "cancelled")
        {
            return Error.Conflict(PrintErrors.DocumentCancelled, "A cancelled invoice is not printed.").WithWhy(("invoice", i.Number));
        }

        if (!proforma && i.Status is not ("posted" or "reversed"))
        {
            return Error.Conflict(PrintErrors.DraftNotTaxInvoice, "A draft is not a tax invoice; print it pro forma or post it first.").WithWhy(("invoice", i.Number), ("status", i.Status));
        }

        var currency = await companies.FindCurrencyAsync(i.Currency, cancellationToken);
        var minor = currency?.MinorUnits ?? 2;
        string Money(decimal amount) => PrintNumbers.Money(amount, minor);
        var isCredit = i.Kind == "credit_note";
        var lines = i.Lines.Select(l => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["line_no"] = l.LineNo,
            ["kind"] = l.Kind,
            ["item_code"] = l.ItemCode,
            ["description"] = l.Description is { Length: > 0 } ? l.Description : l.ItemName is null ? l.AccountRole ?? string.Empty : new LocalizedText(l.ItemName),
            ["source"] = l.ShipmentNumber ?? l.OrderNumber ?? l.CreditedInvoiceNumber,
            ["quantity"] = PrintNumbers.Quantity(l.Quantity),
            ["uom"] = l.UomCode ?? string.Empty,
            ["unit_price"] = PrintNumbers.Price(l.UnitPrice, minor),
            ["discount_pct"] = PrintNumbers.Percent(l.DiscountPct),
            ["discount_amount"] = Money(l.DiscountAmount),
            ["net"] = Money(l.NetAmount),
            ["tax_code"] = l.TaxCode,
            ["tax_rate"] = PrintNumbers.Percent(l.TaxRatePct),
            ["tax_amount"] = Money(l.TaxAmount),
            ["gross"] = Money(l.NetAmount + (l.TaxReverseCharge ? 0m : l.TaxAmount)),
            ["reverse_charge"] = l.TaxReverseCharge,
        }).ToList();
        var taxSummary = i.Lines.Where(static l => l.TaxCode is not null).GroupBy(static l => (l.TaxCode!, l.TaxRatePct)).OrderBy(static g => g.Key.Item1, StringComparer.Ordinal)
            .Select(g => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = g.Key.Item1,
                ["rate"] = PrintNumbers.Percent(g.Key.TaxRatePct),
                ["base"] = Money(g.Sum(static l => l.NetAmount)),
                ["amount"] = Money(g.Sum(static l => l.TaxAmount)),
            }).ToList();
        var instalments = i.OpenItems.OrderBy(static o => o.Instalment).Select(o => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["instalment"] = o.Instalment,
            ["due_date"] = o.DueDate,
            ["amount"] = Money(Math.Abs(o.OriginalTc)),
            ["remaining"] = Money(Math.Abs(o.RemainingTc)),
            ["status"] = o.Status,
        }).ToList();
        var title = isCredit ? LocalizedText.Bilingual("Credit note", "إشعار دائن") : proforma ? LocalizedText.Bilingual("Pro forma invoice", "فاتورة أولية") : LocalizedText.Bilingual("Tax invoice", "فاتورة ضريبية");
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = i.Kind,
            ["is_credit_note"] = isCredit,
            ["is_proforma"] = proforma,
            ["status"] = i.Status,
            ["document_date"] = i.DocumentDate,
            ["posting_date"] = i.PostingDate,
            ["due_date"] = i.DueDate,
            ["currency"] = i.Currency,
            ["exchange_rate"] = PrintNumbers.Rate(i.ExchangeRate),
            ["exchange_rate_shown"] = i.ExchangeRate != 1m && !string.Equals(i.Currency, i.FunctionalCurrency, StringComparison.Ordinal),
            ["functional_currency"] = i.FunctionalCurrency,
            ["customer_reference"] = i.CustomerReference,
            ["credits_invoice_number"] = i.CreditsInvoiceNumber,
            ["payment_terms"] = i.PaymentTermsCode,
            ["notes"] = i.Notes,
            ["prices_include_tax"] = i.PricesIncludeTax,
            ["reverse_charge"] = i.Lines.Any(static l => l.TaxReverseCharge),
            ["has_discount"] = i.Lines.Any(static l => l.DiscountPct != 0m || l.DiscountAmount != 0m),
            ["einvoice_status"] = i.EinvoiceStatus,
            ["qr_payload"] = i.EinvoiceQr,
            ["total_net"] = Money(i.TotalNet),
            ["total_discount"] = Money(i.Lines.Sum(static l => l.DiscountAmount)),
            ["total_tax"] = Money(i.TotalTax),
            ["total_gross"] = Money(i.TotalGross),
            ["amount_in_words_en"] = currency is null ? null : PrintNumbers.Words(i.TotalGross, currency.Value, "en"),
            ["amount_in_words_ar"] = currency is null ? null : PrintNumbers.Words(i.TotalGross, currency.Value, "ar"),
            ["lines"] = lines,
            ["tax_summary"] = taxSummary,
            ["instalments"] = instalments,
        };
        return new PrintModel(i.CompanyId, null, title, i.Number, i.Status, IsFinal: i.Status == "posted" && !proforma, i.DocumentDate, i.Currency, data, i.PartnerId, null, $"{i.Number}.pdf");
    }
}

/// <summary>What a sales order exposes to its print templates: the order confirmation with its lines, what shipped so far and the totals.</summary>
public sealed class SalesOrderPrintModel(OrderService orders, ICompanyDirectory companies, ICurrentPrincipal principal) : IPrintModelProvider
{
    public string DocumentType => OrderService.DocumentType;

    public IReadOnlyList<PrintVariant> Variants { get; } = [new(PrintVariants.Standard, LocalizedText.Bilingual("Order confirmation", "تأكيد الطلب"))];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (principal.Principal is { } who && !who.Has(SalesPermissions.OrderRead))
        {
            return Error.Forbidden("print.forbidden", "Reading sales orders is not permitted.");
        }

        if (subject.DocumentId is not { } id)
        {
            return Error.Validation(PrintErrors.ParameterRequired, "The order to print is named by its id.");
        }

        var o = await orders.GetAsync(id, cancellationToken);
        if (o is null)
        {
            return Error.NotFound(OrderService.DocumentType, id);
        }

        var currency = await companies.FindCurrencyAsync(o.Currency, cancellationToken);
        var minor = currency?.MinorUnits ?? 2;
        string Money(decimal amount) => PrintNumbers.Money(amount, minor);
        var lines = o.Lines.Where(static l => l.Status != "cancelled").Select(l => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["line_no"] = l.LineNo,
            ["item_code"] = l.ItemCode,
            ["description"] = l.Description is { Length: > 0 } ? l.Description : new LocalizedText(l.ItemName),
            ["quantity"] = PrintNumbers.Quantity(l.Quantity),
            ["uom"] = l.UomCode,
            ["unit_price"] = PrintNumbers.Price(l.UnitPrice, minor),
            ["discount_pct"] = PrintNumbers.Percent(l.DiscountPct),
            ["net"] = Money(l.NetAmount),
            ["tax_rate"] = PrintNumbers.Percent(l.TaxRatePct),
            ["tax_amount"] = Money(l.TaxAmount),
            ["gross"] = Money(l.NetAmount + (l.TaxReverseCharge ? 0m : l.TaxAmount)),
            ["qty_shipped"] = PrintNumbers.Quantity(l.QtyShipped),
            ["drop_ship"] = l.DropShip,
        }).ToList();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = o.Status,
            ["status_label"] = StatusLabel(o.Status),
            ["order_date"] = o.OrderDate,
            ["pricing_date"] = o.PricingDate,
            ["currency"] = o.Currency,
            ["notes"] = o.Notes,
            ["has_discount"] = o.Lines.Any(static l => l.DiscountPct != 0m),
            ["total_net"] = Money(o.TotalNet),
            ["total_tax"] = Money(o.TotalTax),
            ["total_gross"] = Money(o.TotalGross),
            ["lines"] = lines,
        };
        return new PrintModel(o.CompanyId, null, LocalizedText.Bilingual("Sales order", "أمر بيع"), o.Number, o.Status, IsFinal: false, o.OrderDate, o.Currency, data, o.PartnerId, null, $"{o.Number}.pdf");
    }

    private static LocalizedText StatusLabel(string status) => status switch
    {
        "draft" => LocalizedText.Bilingual("Draft", "مسودة"),
        "confirmed" => LocalizedText.Bilingual("Confirmed", "مؤكَّد"),
        "on_hold" => LocalizedText.Bilingual("On hold", "معلَّق"),
        "partially_shipped" => LocalizedText.Bilingual("Partially shipped", "شُحن جزئيًا"),
        "shipped" => LocalizedText.Bilingual("Shipped", "تم الشحن"),
        "cancelled" => LocalizedText.Bilingual("Cancelled", "ملغى"),
        _ => LocalizedText.Bilingual(status, status),
    };
}

/// <summary>Numbers as the print models hand them to the templates: invariant digits, the currency's minor units, prices to four places at most.</summary>
internal static class PrintNumbers
{
    public static string Money(decimal amount, int minorUnits) => amount.ToString("N" + Math.Clamp(minorUnits, 0, 6).ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

    public static string Price(decimal price, int minorUnits)
    {
        var scale = Math.Max(minorUnits, Math.Min(4, Scale(price)));
        return price.ToString("N" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Quantity(decimal quantity) => quantity.ToString("#,##0.###", System.Globalization.CultureInfo.InvariantCulture);

    public static string Percent(decimal percent) => percent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public static string Rate(decimal rate) => rate.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The amount in words, or null when the currency's unit names are not known to the speller (the template then leaves the box out).</summary>
    public static string? Words(decimal amount, Currency currency, string language)
    {
        try
        {
            var rounded = RoundingPolicy.Default.Round(Math.Abs(amount), currency.MinorUnits);
            return AmountInWords.Spell(Kernel.Amounts.Money.Of(rounded, currency), language);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int Scale(decimal value)
    {
        var bits = decimal.GetBits(value);
        return (bits[3] >> 16) & 0xFF;
    }
}
