using System.Globalization;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Organization.Contracts;
using Quicker.Printing.Contracts;

namespace Quicker.Receivables.Application;

/// <summary>
/// What a customer statement exposes to its print template (roadmap 5.9): the period, and per currency the opening
/// balance, every document and settlement of the period as a debit or a credit with the running balance, and the
/// closing balance. Printed by company, customer and period (parameters, not a document id); never evidence.
/// </summary>
public sealed class StatementPrintModel(ReceivablesService receivables, ICompanyDirectory companies, ICurrentPrincipal principal) : IPrintModelProvider
{
    public const string DocumentType = "customer_statement";

    string IPrintModelProvider.DocumentType => DocumentType;

    public IReadOnlyList<PrintVariant> Variants { get; } =
    [
        new(PrintVariants.Standard, LocalizedText.Bilingual("Statement of account", "كشف حساب")),
        new(PrintVariants.Brand, LocalizedText.Bilingual("Branded statement", "كشف حساب بهوية الشركة")),
    ];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (principal.Principal is { } who && !who.Has(ReceivablesPermissions.OpenItemRead))
        {
            return Error.Forbidden("print.forbidden", "Reading receivables is not permitted.");
        }

        var parameters = subject.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!parameters.TryGetValue("companyId", out var companyText) || !Guid.TryParse(companyText, out var companyId))
        {
            return Error.Validation(PrintErrors.ParameterRequired, "A statement is printed for a company (companyId).");
        }

        var partnerId = subject.DocumentId;
        if (partnerId is null && parameters.TryGetValue("partnerId", out var partnerText) && Guid.TryParse(partnerText, out var parsed))
        {
            partnerId = parsed;
        }

        if (partnerId is not { } partner)
        {
            return Error.Validation(PrintErrors.ParameterRequired, "A statement is printed for a customer (partnerId).");
        }

        DateOnly? from = parameters.TryGetValue("from", out var fromText) && DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;
        DateOnly? to = parameters.TryGetValue("to", out var toText) && DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        var statement = await receivables.StatementAsync(companyId, partner, from, to, cancellationToken);
        if (statement.IsFailure)
        {
            return statement.Error!;
        }

        var s = statement.Value;
        var company = await companies.FindAsync(new Kernel.Ids.CompanyId(companyId), cancellationToken);
        var currencies = new List<Dictionary<string, object?>>();
        foreach (var c in s.Currencies)
        {
            var minor = (await companies.FindCurrencyAsync(c.Currency, cancellationToken))?.MinorUnits ?? 2;
            string Money(decimal amount) => amount.ToString("N" + Math.Clamp(minor, 0, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            currencies.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["currency"] = c.Currency,
                ["opening"] = Money(c.Opening),
                ["increases"] = Money(c.Increases),
                ["decreases"] = Money(c.Decreases),
                ["closing"] = Money(c.Closing),
                ["lines"] = c.Lines.Select(l => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["date"] = l.Date,
                    ["kind"] = l.Kind,
                    ["document_type"] = DocumentTypeLabel(l.DocumentType),
                    ["document_number"] = l.DocumentNumber,
                    ["reference"] = l.CustomerReference,
                    ["due_date"] = l.DueDate,
                    ["reversal"] = l.Reversal,
                    ["debit"] = l.Amount > 0m ? Money(l.Amount) : string.Empty,
                    ["credit"] = l.Amount < 0m ? Money(-l.Amount) : string.Empty,
                    ["balance"] = Money(l.Balance),
                }).ToList(),
            });
        }

        var period = $"{s.From:yyyy-MM-dd}..{s.To:yyyy-MM-dd}";
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["period_from"] = s.From,
            ["period_to"] = s.To,
            ["currencies"] = currencies,
        };
        return new PrintModel(companyId, null, LocalizedText.Bilingual("Statement of account", "كشف حساب"), $"{s.PartnerCode} {period}", "ready", IsFinal: false,
            s.To, s.Currencies.Count > 0 ? s.Currencies[0].Currency : company?.FunctionalCurrency.Code ?? string.Empty, data, partner, null, $"statement-{s.PartnerCode}-{s.To:yyyy-MM-dd}.pdf");
    }

    private static LocalizedText DocumentTypeLabel(string documentType) => documentType switch
    {
        "sales_invoice" => LocalizedText.Bilingual("Sales invoice", "فاتورة مبيعات"),
        "sales_credit_note" => LocalizedText.Bilingual("Credit note", "إشعار دائن"),
        "bank_receipt" => LocalizedText.Bilingual("Customer receipt", "مقبوضات العملاء"),
        "ar_write_off" => LocalizedText.Bilingual("Write-off", "شطب"),
        "ar_netting" => LocalizedText.Bilingual("Netting", "مقاصة"),
        "ar_dunning_letter" => LocalizedText.Bilingual("Dunning fee", "رسم تذكير"),
        "ar_settlement" => LocalizedText.Bilingual("Settlement", "تسوية"),
        "journal_entry" => LocalizedText.Bilingual("Journal entry", "قيد يومية"),
        _ => LocalizedText.Bilingual(documentType, documentType),
    };
}
