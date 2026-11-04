using System.Text.Json;
using Quicker.Accounting.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Trader.Contracts;

namespace Quicker.Trader.Application;

/// <summary>The two things an owner does with the business's money: put some in (<c>in</c>) or take some out (<c>out</c>).</summary>
public static class OwnerMovementKinds
{
    public const string In = "in";
    public const string Out = "out";
}

/// <summary>Money the owner puts in or takes out: which, from or into which bank account or cash box, how much in the company's currency, on which day, and a note.</summary>
public sealed record TraderOwnerMovementRequest(Guid CompanyId, string Kind, Guid BankAccountId, decimal Amount, DateOnly? Date = null, string? Note = null);

/// <summary>One movement as the trader reads it back: the journal that holds it, its kind, the box, the amount, the note, and whether it still stands.</summary>
public sealed record TraderOwnerMovement(Guid JournalId, string? Number, string Kind, DateOnly Date, Guid? BankAccountId, string BankAccountCode, decimal Amount, string? Note, string Status, Guid? JournalEntryId);

/// <summary>The owner's money in the business (ADR-0035 §12): what was put in and taken out in all, the equity accounts each goes to, and the movements newest first.</summary>
public sealed record TraderOwnerMoney(Guid CompanyId, string Currency, decimal PutIn, decimal TakenOut, string CapitalAccount, string DrawingsAccount, IReadOnlyList<TraderOwnerMovement> Movements);

/// <summary>
/// The owner's capital and drawings from the trader's screens (ADR-0035 §12, A-195): each movement is a manual journal
/// posted through Accounting (Dr the box / Cr capital for money put in; Dr drawings / Cr the box for money taken out),
/// so the box's balance and the equity accounts move together and the journal is numbered, listed and reversible in the
/// full edition. The equity accounts are settings with the chart's usual codes as defaults; nothing is kept here.
/// </summary>
public sealed class OwnerMoneyService(ICompanyDirectory companies, ICompanySettings settings, IChartOfAccounts chart, IBankAccountDirectory banks, IJournals journals, IClock clock)
{
    public const string ReferencePrefix = "OWNER-";
    private const string DefaultCapitalAccount = "3100";
    private const string DefaultDrawingsAccount = "3600";
    private const string InEn = "Owner put money in";
    private const string InAr = "المالك أودع مالاً في المشروع";
    private const string OutEn = "Owner took money out";
    private const string OutAr = "المالك سحب مالاً من المشروع";

    public async Task<Result<TraderOwnerMoney>> ReadAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var boxes = (await banks.ListAsync(companyId, cancellationToken)).ToDictionary(static b => b.Id);
        var movements = (await journals.ListByReferenceAsync(companyId, ReferencePrefix, 500, cancellationToken))
            .Select(j => Map(j, boxes))
            .OfType<TraderOwnerMovement>()
            .ToList();
        var standing = movements.Where(static m => m.Status == "posted").ToList();
        return new TraderOwnerMoney(companyId, company.FunctionalCurrency.Code,
            standing.Where(static m => m.Kind == OwnerMovementKinds.In).Sum(static m => m.Amount),
            standing.Where(static m => m.Kind == OwnerMovementKinds.Out).Sum(static m => m.Amount),
            await AccountCodeAsync(companyId, TraderSettingKeys.OwnerCapitalAccount, DefaultCapitalAccount, cancellationToken),
            await AccountCodeAsync(companyId, TraderSettingKeys.OwnerDrawingsAccount, DefaultDrawingsAccount, cancellationToken),
            movements);
    }

    public async Task<Result<TraderOwnerMoney>> RecordAsync(TraderOwnerMovementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not (OwnerMovementKinds.In or OwnerMovementKinds.Out))
        {
            return Error.Validation("owner.kind_invalid", "Money is put in or taken out.").WithWhy(("kind", request.Kind));
        }

        if (request.Amount <= 0m)
        {
            return Error.Validation("owner.amount_invalid", "The amount is above zero.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var fc = company.FunctionalCurrency.Code;
        var box = await banks.FindAsync(request.BankAccountId, cancellationToken);
        if (box is null || box.CompanyId != request.CompanyId)
        {
            return Error.NotFound("bank_account", request.BankAccountId);
        }

        if (!box.IsActive)
        {
            return Error.Validation("owner.bank_account_inactive", $"'{box.Code}' is closed.").WithWhy(("bankAccount", box.Code));
        }

        if (!string.Equals(box.Currency, fc, StringComparison.Ordinal))
        {
            return Error.Validation("owner.box_currency", $"'{box.Code}' is kept in {box.Currency}; the owner's money moves through an account in {fc}.").WithWhy(("bankAccount", box.Code), ("bankCurrency", box.Currency), ("currency", fc));
        }

        var chartId = await chart.ChartForCompanyAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (chartId is null)
        {
            return Error.Validation("owner.chart_required", "The company has no chart of accounts yet.");
        }

        var boxAccount = await chart.FindAccountAsync(box.GlAccountId, cancellationToken);
        if (boxAccount is null)
        {
            return Error.Validation("owner.box_account_missing", $"'{box.Code}' has no ledger account.").WithWhy(("bankAccount", box.Code));
        }

        var putIn = kind == OwnerMovementKinds.In;
        var equityCode = await AccountCodeAsync(request.CompanyId, putIn ? TraderSettingKeys.OwnerCapitalAccount : TraderSettingKeys.OwnerDrawingsAccount, putIn ? DefaultCapitalAccount : DefaultDrawingsAccount, cancellationToken);
        var equity = await chart.FindAccountByCodeAsync(chartId.Value, equityCode, cancellationToken);
        if (equity is null || equity.IsHeader || !equity.IsActive)
        {
            return Error.Validation("owner.account_missing", $"The company's chart has no postable account '{equityCode}' for the owner's money {(putIn ? "put in" : "taken out")}; set the account under Settings.").WithWhy(("kind", kind), ("account", equityCode), ("setting", putIn ? TraderSettingKeys.OwnerCapitalAccount : TraderSettingKeys.OwnerDrawingsAccount));
        }

        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var description = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = note ?? (putIn ? InEn : OutEn), ["ar"] = note ?? (putIn ? InAr : OutAr) };
        var lines = putIn
            ? new[] { new OpeningJournalLine(boxAccount.Code, request.Amount, 0m, BankAccountId: box.Id), new OpeningJournalLine(equity.Code, 0m, request.Amount) }
            : new[] { new OpeningJournalLine(equity.Code, request.Amount, 0m), new OpeningJournalLine(boxAccount.Code, 0m, request.Amount, BankAccountId: box.Id) };
        var posted = await journals.PostAsync(new ModuleJournalRequest(request.CompanyId, date, lines, description, ReferencePrefix + (putIn ? "IN" : "OUT")), cancellationToken);
        return posted.IsSuccess ? await ReadAsync(request.CompanyId, cancellationToken) : posted.Error!;
    }

    private async Task<string> AccountCodeAsync(Guid companyId, string key, string fallback, CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(companyId, key, cancellationToken);
        var code = value is { ValueKind: JsonValueKind.String } text ? text.GetString()?.Trim() : null;
        return string.IsNullOrEmpty(code) ? fallback : code;
    }

    /// <summary>A journal of ours as a movement: the kind from its reference, the box and the amount from its bank line, the note from its description unless that is the stock wording.</summary>
    private static TraderOwnerMovement? Map(JournalBrief journal, IReadOnlyDictionary<Guid, BankAccountInfo> boxes)
    {
        var kind = journal.Reference switch
        {
            ReferencePrefix + "IN" => OwnerMovementKinds.In,
            ReferencePrefix + "OUT" => OwnerMovementKinds.Out,
            _ => null,
        };
        var bankLine = journal.Lines.FirstOrDefault(static l => l.SubledgerType == SubledgerTypes.Bank);
        if (kind is null || bankLine is null)
        {
            return null;
        }

        var box = bankLine.SubledgerRef is { } id ? boxes.GetValueOrDefault(id) : null;
        var en = journal.Description.GetValueOrDefault("en");
        var note = en is null or InEn or OutEn ? null : en;
        return new TraderOwnerMovement(journal.JournalId, journal.Number, kind, journal.PostingDate, bankLine.SubledgerRef, box?.Code ?? string.Empty, kind == OwnerMovementKinds.In ? bankLine.Debit : bankLine.Credit, note, journal.Status, journal.JournalEntryId);
    }
}
