using Microsoft.EntityFrameworkCore;
using Quicker.Einvoicing.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Sales.Persistence;
using Quicker.Tax.Contracts;

namespace Quicker.Sales.Application;

/// <summary>
/// Hands a posted sales invoice or credit note to the e-invoicing adapter (A-179): the seller from the company and
/// its VAT registration in the scheme's regime, the buyer from the partner with its VAT registration and address, the
/// lines with their tax category from the tax code's treatment, and the credited invoice for a credit note.
/// </summary>
public sealed class SalesEinvoiceSource(SalesDbContext db, ICompanyDirectory companies, IPartnerDirectory partners, IItemDirectory items, ITaxDirectory taxDirectory, IClock clock) : IEinvoiceSource
{
    public string DocumentType => InvoiceService.DocumentType;

    public async Task<Result<EinvoiceDocument>> LoadAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == documentId, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(InvoiceService.DocumentType, documentId);
        }

        if (invoice.Status != "posted")
        {
            return Error.Conflict("invoice.not_posted", "Only a posted invoice is handed to the e-invoicing scheme.").WithWhy(("status", invoice.Status));
        }

        if (invoice.EinvoiceScheme is null)
        {
            return Error.Conflict("invoice.einvoice_not_required", "The invoice's tax regime names no e-invoicing scheme.");
        }

        var company = await companies.FindAsync(new CompanyId(invoice.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("einvoice.company_unknown", "The company does not exist.").WithWhy(("companyId", invoice.CompanyId));
        }

        var registration = await taxDirectory.FindRegistrationAsync(invoice.CompanyId, invoice.EinvoiceScheme, cancellationToken);
        var address = company.Address ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = company.RegistrationNumbers ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var seller = new EinvoiceParty(
            Text(company.LegalName, company.DefaultLanguage), registration?.RegistrationNumber, numbers.GetValueOrDefault("crn"), "CRN",
            address.GetValueOrDefault("street") ?? address.GetValueOrDefault("line1"), address.GetValueOrDefault("buildingNumber"), address.GetValueOrDefault("plot"), address.GetValueOrDefault("district"), address.GetValueOrDefault("city"), address.GetValueOrDefault("postalCode"), company.Country);

        var partner = await partners.FindAsync(invoice.PartnerId, cancellationToken);
        EinvoiceParty? buyer = null;
        if (partner is not null)
        {
            var registrations = await partners.TaxRegistrationsAsync(partner.Id, cancellationToken);
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            var vat = registrations.Where(r => r.RegistrationType == "vat" && (r.ValidTo is null || r.ValidTo >= today)).OrderByDescending(r => string.Equals(r.Country, company.Country, StringComparison.OrdinalIgnoreCase)).Select(static r => r.Number).FirstOrDefault();
            var crn = registrations.Where(static r => r.RegistrationType == "crn").Select(static r => r.Number).FirstOrDefault();
            var billing = (await partners.AddressesAsync(partner.Id, cancellationToken)).OrderByDescending(static a => a.Role == "billing").ThenByDescending(static a => a.IsDefault).FirstOrDefault();
            var parts = billing?.Address ?? new Dictionary<string, string>(StringComparer.Ordinal);
            buyer = new EinvoiceParty(
                Text(partner.LegalName, company.DefaultLanguage), vat, crn, "CRN",
                parts.GetValueOrDefault("street") ?? parts.GetValueOrDefault("line1"), parts.GetValueOrDefault("buildingNumber"), parts.GetValueOrDefault("plot"), parts.GetValueOrDefault("district"), parts.GetValueOrDefault("city"), parts.GetValueOrDefault("postalCode"), billing?.Country ?? company.Country);
        }

        var codeIds = invoice.Lines.Where(static l => l.TaxCodeId is not null).Select(static l => l.TaxCodeId!.Value).Distinct().ToList();
        var codes = await taxDirectory.DescribeCodesAsync(codeIds, cancellationToken);
        var lines = new List<EinvoiceLine>(invoice.Lines.Count);
        foreach (var line in invoice.Lines)
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
            var name = line.Description;
            if (string.IsNullOrWhiteSpace(name) && line.ItemId is { } itemId)
            {
                var item = await items.FindAsync(itemId, cancellationToken);
                name = item is null ? null : Text(item.Name, company.DefaultLanguage);
            }

            var quantity = Math.Abs(line.Quantity);
            var net = Math.Abs(line.NetAmount);
            var discount = Math.Abs(line.DiscountAmount);
            lines.Add(new EinvoiceLine(line.LineNo, string.IsNullOrWhiteSpace(name) ? $"Line {line.LineNo}" : name, quantity == 0m ? 1m : quantity, "PCE", quantity == 0m ? net : Math.Abs(line.UnitPrice), discount, net, category, line.TaxRatePct, Math.Abs(line.TaxAmount),
                category == EinvoiceTaxCategories.Standard ? null : code?.ExemptionReasonCode, category == EinvoiceTaxCategories.Standard ? null : code is null || code.ExemptionReason.IsEmpty ? null : code.ExemptionReason.Resolve("en")));
        }

        var kind = invoice.Kind == "credit_note" ? EinvoiceDocumentKinds.CreditNote : EinvoiceDocumentKinds.Invoice;
        string? creditedNumber = null;
        DateOnly? creditedDate = null;
        if (invoice.CreditsInvoiceId is { } creditedId)
        {
            var credited = await db.Invoices.AsNoTracking().Where(i => i.Id == creditedId).Select(static i => new { i.Number, i.DocumentDate }).SingleOrDefaultAsync(cancellationToken);
            creditedNumber = credited?.Number;
            creditedDate = credited?.DocumentDate;
        }

        var issuedAt = IssuedAt(invoice.PostedAt ?? invoice.CreatedAt, company.TimeZone);
        return new EinvoiceDocument(
            InvoiceService.DocumentType, invoice.Id, invoice.CompanyId, invoice.Number, kind, invoice.DocumentDate, issuedAt, invoice.Currency, seller, buyer, lines,
            Math.Abs(invoice.TotalNet), Math.Abs(invoice.TotalTax), Math.Abs(invoice.TotalGross), invoice.Notes, creditedNumber, creditedDate,
            kind == EinvoiceDocumentKinds.CreditNote ? (string.IsNullOrWhiteSpace(invoice.Notes) ? "Credit note" : invoice.Notes) : null, invoice.DocumentDate, invoice.DueDate is { } due && due <= invoice.DocumentDate ? "10" : "30");
    }

    private static string Text(LocalizedText text, string language) => text.Resolve(language, "en");

    /// <summary>The posting instant in the company's own clock, since the scheme reads the issue time as local time.</summary>
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

/// <summary>What a posted invoice under an e-invoicing scheme enqueues: its own submission, done by the worker so posting never waits on the scheme.</summary>
public sealed record EinvoiceSubmitPayload(Guid InvoiceId);

/// <summary>
/// Submits a posted invoice to its scheme in the background. A device set to submit by hand, a company with no
/// device yet, or a regime with no adapter leaves the invoice pending for a person; the scheme being unreachable
/// retries with the job's backoff; a refusal by the scheme is recorded on the invoice and is not a failure here.
/// </summary>
public sealed class EinvoiceSubmitJob(InvoiceService invoices) : IJobHandler<EinvoiceSubmitPayload>
{
    public static string JobType => "sales.invoices.einvoice";

    private static readonly HashSet<string> LeftForAPerson = new(StringComparer.Ordinal)
    {
        "einvoice.no_device", "einvoice.device_not_active", "einvoice.manual_only", "einvoice.simplified_not_enabled", "einvoice.standard_not_enabled", "einvoice.not_registered",
        "tax.clearance_not_configured", "tax.clearance_not_required", "invoice.not_posted", "invoice.einvoice_not_required", "sales_invoice.not_found",
    };

    public async Task<object?> ExecuteAsync(EinvoiceSubmitPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var submitted = await invoices.SubmitEinvoiceAsync(payload.InvoiceId, automatic: true, cancellationToken);
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
            throw new InvalidOperationException($"The e-invoicing scheme could not be reached for invoice {payload.InvoiceId}; retrying.");
        }

        throw new JobFailedException($"Invoice {payload.InvoiceId} could not be submitted: {code} {submitted.Error.Message}");
    }
}
