using System.Globalization;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Organization.Contracts;
using Quicker.Printing.Contracts;
using Quicker.Receivables.Contracts;

namespace Quicker.Banking.Application;

/// <summary>
/// What a customer receipt exposes to its print templates (T12b, ADR-0022's data contract): the money received, from
/// whom, into which box or account and how, the invoices it settled with what remains on each, what the customer still
/// owes after it, and the amount in words. A posted receipt is final (its first render is kept as evidence); a draft
/// prints with the draft watermark and a reversed one with the reversed watermark.
/// </summary>
public sealed class ReceiptPrintModel(CustomerReceiptService receipts, BankAccountService accounts, ICompanyDirectory companies, IReceivables receivables, ICurrentPrincipal principal) : IPrintModelProvider
{
    public string DocumentType => CustomerReceiptService.DocumentType;

    public IReadOnlyList<PrintVariant> Variants { get; } =
    [
        new(PrintVariants.Standard, LocalizedText.Bilingual("Payment receipt", "سند قبض")),
        new(PrintVariants.Brand, LocalizedText.Bilingual("Branded receipt", "سند قبض بهوية الشركة")),
    ];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (principal.Principal is { } who && !who.Has(BankingPermissions.ReceiptRead))
        {
            return Error.Forbidden("print.forbidden", "Reading receipts is not permitted.");
        }

        if (subject.DocumentId is not { } id)
        {
            return Error.Validation(PrintErrors.ParameterRequired, "A receipt is printed by its id.");
        }

        var found = await receipts.GetAsync(id, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var r = found.Value;
        var currency = await companies.FindCurrencyAsync(r.Currency, cancellationToken);
        var minor = currency?.MinorUnits ?? 2;
        string Money(decimal amount) => amount.ToString("N" + Math.Clamp(minor, 0, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var account = await accounts.FindAsync(r.BankAccountId, cancellationToken);
        var balanceAfter = r.Status == "posted" ? await receivables.OpenBalanceAsync(r.CompanyId, r.PartnerId, r.Currency, cancellationToken) : (decimal?)null;

        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = r.Status,
            ["receipt_date"] = r.PaymentDate,
            ["currency"] = r.Currency,
            ["bank_account_code"] = r.BankAccountCode,
            ["bank_account"] = account is null ? r.BankAccountCode : LocalizedText.Bilingual($"{account.Code} · {account.Name.Resolve("en")}", $"{account.Code} · {account.Name.Resolve("ar")}"),
            ["method_code"] = r.Method,
            ["method"] = MethodLabel(r.Method),
            ["reference"] = r.Reference,
            ["amount"] = Money(r.AmountTc),
            ["on_account"] = Money(r.OnAccountTc),
            ["on_account_shown"] = r.OnAccountTc != 0m,
            ["discount"] = Money(r.DiscountTc),
            ["has_discount"] = r.DiscountTc != 0m,
            ["wht"] = Money(r.WhtTc),
            ["has_lines"] = r.Lines.Count > 0,
            ["lines"] = r.Lines.Select(l => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["document_number"] = l.DocumentNumber,
                ["instalment"] = l.Instalment,
                ["due_date"] = l.DueDate,
                ["amount"] = Money(l.AmountTc),
                ["discount"] = Money(l.DiscountTc),
                ["remaining"] = Money(l.ItemRemainingTc),
            }).ToList(),
            ["balance_after"] = balanceAfter is { } b ? Money(b) : null,
            ["amount_in_words_en"] = currency is { } spelled ? Words(r.AmountTc, spelled, "en") : null,
            ["amount_in_words_ar"] = currency is { } spelledAr ? Words(r.AmountTc, spelledAr, "ar") : null,
            ["notes"] = r.Notes,
            ["cheque"] = r.Cheque is { } c ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["number"] = c.ChequeNumber, ["bank"] = c.DrawerBank, ["due_date"] = c.DueDate } : null,
        };
        return new PrintModel(r.CompanyId, null, LocalizedText.Bilingual("Payment receipt", "سند قبض"), r.Number, r.Status, IsFinal: r.Status == "posted", r.PaymentDate, r.Currency, data, r.PartnerId, null, $"{r.Number}.pdf");
    }

    private static LocalizedText MethodLabel(string method) => method switch
    {
        "cash" => LocalizedText.Bilingual("Cash", "نقداً"),
        "transfer" => LocalizedText.Bilingual("Bank transfer", "حوالة مصرفية"),
        "cheque" => LocalizedText.Bilingual("Cheque", "شيك"),
        "card" => LocalizedText.Bilingual("Card", "بطاقة"),
        _ => LocalizedText.Bilingual(method, method),
    };

    /// <summary>The amount in words, or null when the currency's unit names are not known to the speller (the template then leaves the line out).</summary>
    private static string? Words(decimal amount, Currency currency, string language)
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
    }
}
