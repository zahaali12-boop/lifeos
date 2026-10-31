using Microsoft.EntityFrameworkCore;
using Quicker.Einvoicing.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Pos.Contracts;
using Quicker.Pos.Persistence;
using Quicker.Tax.Contracts;

namespace Quicker.Pos.Application;

/// <summary>
/// Hands a posted till order to the e-invoicing adapter (A-180): a sale is an invoice, a refund a credit note of the
/// sale it gives back; the seller is the company with its VAT registration in the scheme's regime; the buyer is the
/// named customer with a VAT registration (a standard invoice) or no one (a simplified invoice, the retail case).
/// </summary>
public sealed class PosEinvoiceSource(PosDbContext db, ICompanyDirectory companies, IPartnerDirectory partners, ITaxDirectory taxDirectory, IClock clock) : IEinvoiceSource
{
    public string DocumentType => PosDocumentTypes.Order;

    public async Task<Result<EinvoiceDocument>> LoadAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines.OrderBy(static l => l.LineNo)).Include(static o => o.Payments).SingleOrDefaultAsync(o => o.Id == documentId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(PosDocumentTypes.Order, documentId);
        }

        if (order.EinvoiceScheme is null)
        {
            return Error.Conflict("pos.einvoice_not_required", "The order's tax regime names no e-invoicing scheme.");
        }

        var company = await companies.FindAsync(new CompanyId(order.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("einvoice.company_unknown", "The company does not exist.").WithWhy(("companyId", order.CompanyId));
        }

        var registration = await taxDirectory.FindRegistrationAsync(order.CompanyId, order.EinvoiceScheme, cancellationToken);
        var address = company.Address ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = company.RegistrationNumbers ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var seller = new EinvoiceParty(
            Text(company.LegalName, company.DefaultLanguage), registration?.RegistrationNumber, numbers.GetValueOrDefault("crn"), "CRN",
            address.GetValueOrDefault("street") ?? address.GetValueOrDefault("line1"), address.GetValueOrDefault("buildingNumber"), address.GetValueOrDefault("plot"), address.GetValueOrDefault("district"), address.GetValueOrDefault("city"), address.GetValueOrDefault("postalCode"), company.Country);

        // Only a customer registered for VAT makes the receipt a standard invoice; a walk-in customer gets a simplified one.
        EinvoiceParty? buyer = null;
        if (order.PartnerId is { } partnerId && await partners.FindAsync(partnerId, cancellationToken) is { } partner)
        {
            var registrations = await partners.TaxRegistrationsAsync(partner.Id, cancellationToken);
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            var vat = registrations.Where(r => r.RegistrationType == "vat" && (r.ValidTo is null || r.ValidTo >= today)).OrderByDescending(r => string.Equals(r.Country, company.Country, StringComparison.OrdinalIgnoreCase)).Select(static r => r.Number).FirstOrDefault();
            if (vat is not null)
            {
                var crn = registrations.Where(static r => r.RegistrationType == "crn").Select(static r => r.Number).FirstOrDefault();
                var billing = (await partners.AddressesAsync(partner.Id, cancellationToken)).OrderByDescending(static a => a.Role == "billing").ThenByDescending(static a => a.IsDefault).FirstOrDefault();
                var parts = billing?.Address ?? new Dictionary<string, string>(StringComparer.Ordinal);
                buyer = new EinvoiceParty(
                    Text(partner.LegalName, company.DefaultLanguage), vat, crn, "CRN",
                    parts.GetValueOrDefault("street") ?? parts.GetValueOrDefault("line1"), parts.GetValueOrDefault("buildingNumber"), parts.GetValueOrDefault("plot"), parts.GetValueOrDefault("district"), parts.GetValueOrDefault("city"), parts.GetValueOrDefault("postalCode"), billing?.Country ?? company.Country);
            }
        }

        var codeIds = order.Lines.Where(static l => l.TaxCodeId is not null).Select(static l => l.TaxCodeId!.Value).Distinct().ToList();
        var codes = await taxDirectory.DescribeCodesAsync(codeIds, cancellationToken);
        var lines = new List<EinvoiceLine>(order.Lines.Count);
        foreach (var line in order.Lines)
        {
            var code = line.TaxCodeId is { } codeId ? codes.GetValueOrDefault(codeId) : null;
            var category = code?.Treatment switch
            {
                TaxTreatments.ZeroRated => EinvoiceTaxCategories.ZeroRated,
                TaxTreatments.Exempt => EinvoiceTaxCategories.Exempt,
                TaxTreatments.OutOfScope => EinvoiceTaxCategories.OutOfScope,
                null => EinvoiceTaxCategories.OutOfScope,
                _ => EinvoiceTaxCategories.Standard,
            };
            var name = Text(line.Description, company.DefaultLanguage);
            // The scheme reads the tax on the net of the line; prices that include tax are split the way the tax engine split them.
            var unitNet = line.Quantity == 0m ? line.NetAmount : (line.NetAmount + line.DiscountAmount) / line.Quantity;
            lines.Add(new EinvoiceLine(line.LineNo, string.IsNullOrWhiteSpace(name) ? line.ItemCode : name, line.Quantity, "PCE", unitNet, line.DiscountAmount, line.NetAmount, category, line.TaxRatePct, line.TaxAmount,
                category == EinvoiceTaxCategories.Standard ? null : code?.ExemptionReasonCode, category == EinvoiceTaxCategories.Standard ? null : code is null || code.ExemptionReason.IsEmpty ? null : code.ExemptionReason.Resolve("en")));
        }

        var refund = order.Kind == PosOrderKinds.Refund;
        string? refundedNumber = null;
        DateOnly? refundedDate = null;
        if (order.RefundsOrderId is { } refundsId)
        {
            var refunded = await db.Orders.AsNoTracking().Where(o => o.Id == refundsId).Select(static o => new { o.Number, o.OrderDate }).SingleOrDefaultAsync(cancellationToken);
            refundedNumber = refunded?.Number;
            refundedDate = refunded?.OrderDate;
        }

        var paymentMeans = order.Payments.Any(static p => p.Method == PosPaymentMethods.Credit) ? "30" : order.Payments.Any(static p => p.Method == PosPaymentMethods.Card) ? "48" : "10";
        return new EinvoiceDocument(
            PosDocumentTypes.Order, order.Id, order.CompanyId, order.Number, refund ? EinvoiceDocumentKinds.CreditNote : EinvoiceDocumentKinds.Invoice, order.OrderDate, IssuedAt(order.OrderedAt, company.TimeZone), order.Currency, seller, buyer, lines,
            order.TotalNet, order.TotalTax, order.TotalGross, order.Notes, refundedNumber, refundedDate, refund ? "Refund" : null, order.OrderDate, paymentMeans);
    }

    private static string Text(LocalizedText text, string language) => text.Resolve(language, "en");

    private static DateTimeOffset IssuedAt(DateTimeOffset utc, string timeZone)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (TimeZoneNotFoundException)
        {
            return utc;
        }
        catch (InvalidTimeZoneException)
        {
            return utc;
        }
    }
}

/// <summary>What a posted till order under an e-invoicing scheme enqueues: its own submission, done by the worker so the sync never waits on the scheme.</summary>
public sealed record PosEinvoicePayload(Guid OrderId);

/// <summary>Submits a till order to its scheme in the background; the same outcomes as a sales invoice's job: left for a person, retried when unreachable, failed otherwise.</summary>
public sealed class PosEinvoiceJob(OrderService orders) : IJobHandler<PosEinvoicePayload>
{
    public static string JobType => "pos.orders.einvoice";

    private static readonly HashSet<string> LeftForAPerson = new(StringComparer.Ordinal)
    {
        "einvoice.no_device", "einvoice.device_not_active", "einvoice.manual_only", "einvoice.simplified_not_enabled", "einvoice.standard_not_enabled", "einvoice.not_registered",
        "tax.clearance_not_configured", "tax.clearance_not_required", "pos.einvoice_not_required", "pos_order.not_found",
    };

    public async Task<object?> ExecuteAsync(PosEinvoicePayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var submitted = await orders.SubmitEinvoiceAsync(payload.OrderId, automatic: true, cancellationToken);
        if (submitted.IsSuccess)
        {
            return new { submitted = true, status = submitted.Value.EinvoiceStatus };
        }

        var code = submitted.Error!.Code;
        if (LeftForAPerson.Contains(code))
        {
            return new { submitted = false, reason = code };
        }

        if (code == "einvoice.unreachable")
        {
            throw new InvalidOperationException($"The e-invoicing scheme could not be reached for order {payload.OrderId}; retrying.");
        }

        throw new JobFailedException($"Order {payload.OrderId} could not be submitted: {code} {submitted.Error.Message}");
    }
}
