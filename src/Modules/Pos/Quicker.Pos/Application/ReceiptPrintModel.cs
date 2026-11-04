using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Organization.Contracts;
using Quicker.Pos.Contracts;
using Quicker.Pos.Persistence;
using Quicker.Printing.Contracts;

namespace Quicker.Pos.Application;

/// <summary>What a till order exposes to its receipt template (ADR-0022): the lines, the tenders and change, the totals with the rounding, the cashier and the register, the e-invoice QR when the scheme gave one.</summary>
public sealed class ReceiptPrintModel(PosDbContext db, OrderService orders, ICompanyDirectory companies, ICurrentPrincipal principal) : IPrintModelProvider
{
    public string DocumentType => PosDocumentTypes.Order;

    public IReadOnlyList<PrintVariant> Variants { get; } = [new(PrintVariants.Standard, LocalizedText.Bilingual("Receipt", "إيصال"))];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (principal.Principal is { } who && !who.Has(PosPermissions.OrderRead))
        {
            return Error.Forbidden("print.forbidden", "Reading till orders is not permitted.");
        }

        if (subject.DocumentId is not { } id)
        {
            return Error.Validation(PrintErrors.ParameterRequired, "The order to print is named by its id.");
        }

        var order = await orders.LoadAsync(id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(PosDocumentTypes.Order, id);
        }

        var register = await db.Registers.AsNoTracking().SingleAsync(r => r.Id == order.RegisterId, cancellationToken);
        var session = await db.Sessions.AsNoTracking().Where(s => s.Id == order.SessionId).Select(static s => new { s.Number, s.CashierName }).SingleAsync(cancellationToken);
        var refunded = order.RefundsOrderId is { } refundsId ? await db.Orders.AsNoTracking().Where(o => o.Id == refundsId).Select(static o => o.Number).SingleOrDefaultAsync(cancellationToken) : null;
        var currency = await companies.FindCurrencyAsync(order.Currency, cancellationToken);
        var minor = currency?.MinorUnits ?? 2;
        string Money(decimal amount) => amount.ToString("N" + Math.Clamp(minor, 0, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var refund = order.Kind == PosOrderKinds.Refund;
        var lines = order.Lines.OrderBy(static l => l.LineNo).Select(l => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["line_no"] = l.LineNo,
            ["item_code"] = l.ItemCode,
            ["description"] = l.Description,
            ["quantity"] = l.Quantity.ToString("#,##0.###", CultureInfo.InvariantCulture),
            ["uom"] = l.UomCode,
            ["unit_price"] = Money(l.UnitPrice),
            ["discount_pct"] = l.DiscountPct.ToString("0.##", CultureInfo.InvariantCulture),
            ["discount_amount"] = Money(l.DiscountAmount),
            ["has_discount"] = l.DiscountAmount != 0m,
            ["net"] = Money(l.NetAmount),
            ["tax_rate"] = l.TaxRatePct.ToString("0.##", CultureInfo.InvariantCulture),
            ["tax_amount"] = Money(l.TaxAmount),
            ["gross"] = Money(l.GrossAmount),
        }).ToList();
        var payments = order.Payments.OrderBy(static p => p.LineNo).Select(p => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["method"] = p.Method,
            ["method_label"] = p.Method switch
            {
                PosPaymentMethods.Cash => LocalizedText.Bilingual("Cash", "نقدًا"),
                PosPaymentMethods.Card => LocalizedText.Bilingual("Card", "بطاقة"),
                _ => LocalizedText.Bilingual("On account", "على الحساب"),
            },
            ["amount"] = Money(p.Amount),
            ["reference"] = p.Reference,
        }).ToList();
        var taxSummary = order.Lines.Where(static l => l.TaxCodeId is not null).GroupBy(static l => l.TaxRatePct).OrderBy(static g => g.Key).Select(g => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rate"] = g.Key.ToString("0.##", CultureInfo.InvariantCulture),
            ["base"] = Money(g.Sum(static l => l.NetAmount)),
            ["amount"] = Money(g.Sum(static l => l.TaxAmount)),
        }).ToList();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = order.Kind,
            ["is_refund"] = refund,
            ["status"] = "posted",
            ["document_date"] = order.OrderDate,
            ["ordered_at"] = order.OrderedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["currency"] = order.Currency,
            ["register"] = register.Name,
            ["register_code"] = register.Code,
            ["session"] = session.Number,
            ["cashier"] = session.CashierName,
            ["refunds_number"] = refunded,
            ["prices_include_tax"] = order.PricesIncludeTax,
            ["has_discount"] = order.Lines.Any(static l => l.DiscountAmount != 0m),
            ["has_tax"] = order.TotalTax != 0m,
            ["has_rounding"] = order.Rounding != 0m,
            ["total_net"] = Money(order.TotalNet),
            ["total_discount"] = Money(order.Lines.Sum(static l => l.DiscountAmount)),
            ["total_tax"] = Money(order.TotalTax),
            ["total_gross"] = Money(order.TotalGross),
            ["rounding"] = Money(order.Rounding),
            ["total_due"] = Money(order.TotalDue),
            ["total_paid"] = Money(order.TotalPaid),
            ["change_given"] = Money(order.ChangeGiven),
            ["has_change"] = order.ChangeGiven != 0m,
            ["einvoice_status"] = order.EinvoiceStatus,
            ["qr_payload"] = order.EinvoiceQr,
            ["footer"] = register.ReceiptFooter.IsEmpty ? null : register.ReceiptFooter,
            ["notes"] = order.Notes,
            ["lines"] = lines,
            ["payments"] = payments,
            ["tax_summary"] = taxSummary,
        };
        var title = refund ? LocalizedText.Bilingual("Refund receipt", "إيصال استرجاع") : order.TotalTax != 0m ? LocalizedText.Bilingual("Simplified tax invoice", "فاتورة ضريبية مبسطة") : LocalizedText.Bilingual("Receipt", "إيصال");
        return new PrintModel(order.CompanyId, register.BranchId, title, order.Number, "posted", IsFinal: true, order.OrderDate, order.Currency, data, order.PartnerId, null, $"{order.Number}.pdf");
    }
}
