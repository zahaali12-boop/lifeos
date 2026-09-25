using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Accounting.Application;
using Quicker.Accounting.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Web;

namespace Quicker.Accounting.Api;

/// <summary>Posting groups, posting profiles, postings, journal entries and balances under /api/v1/accounting.</summary>
public static class PostingEndpoints
{
    public static RouteGroupBuilder MapPostingEndpoints(this RouteGroupBuilder accounting)
    {
        ArgumentNullException.ThrowIfNull(accounting);

        var groups = accounting.MapGroup("/posting-groups");
        groups.MapGet("/", async (string? kind, ProfileService service, CancellationToken ct) => TypedResults.Ok(await service.ListGroupsAsync(kind, ct)))
            .RequirePermission(AccountingPermissions.ProfileRead);
        groups.MapPost("/", async (SavePostingGroupRequest request, ProfileService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveGroupAsync(null, request, ct), static g => $"/api/v1/accounting/posting-groups/{g.Id}"))
            .RequirePermission(AccountingPermissions.ProfileManage)
            .WithSummary("Kinds: item, partner_customer, partner_supplier, bank, asset, tax, charge");
        groups.MapPut("/{groupId:guid}", async (Guid groupId, SavePostingGroupRequest request, ProfileService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveGroupAsync(groupId, request, ct)))
            .RequirePermission(AccountingPermissions.ProfileManage);

        var company = accounting.MapGroup("/companies/{companyId:guid}");
        company.MapGet("/posting-profiles", async (Guid companyId, ProfileService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListProfilesAsync(companyId, ct)))
            .RequirePermission(AccountingPermissions.ProfileRead);
        company.MapPost("/posting-profiles", async (Guid companyId, SavePostingProfileRequest request, ProfileService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateProfileAsync(companyId, request, ct), static p => $"/api/v1/accounting/posting-profiles/{p.Id}"))
            .RequirePermission(AccountingPermissions.ProfileManage)
            .WithSummary("An empty draft profile (next version of the code)");
        company.MapPost("/posting-profiles/from-chart", async (Guid companyId, SavePostingProfileRequest? request, ProfileService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateFromChartAsync(companyId, request, ct), static p => $"/api/v1/accounting/posting-profiles/{p.Id}"))
            .RequirePermission(AccountingPermissions.ProfileManage)
            .WithSummary("An active profile with one default rule per role from the chart's default accounts; unresolvedRoles lists what the chart does not name");
        company.MapGet("/journal-entries", async (Guid companyId, DateOnly? from, DateOnly? to, string? sourceDocumentType, string? number, Guid? accountId, bool? isManual, decimal? minAmount, string? text, int? limit, string? cursor, JournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListEntriesAsync(companyId, from, to, sourceDocumentType, new PageRequest(limit, cursor), ct, new JournalBrowserFilter(number, accountId, isManual, minAmount, text))))
            .RequirePermission(AccountingPermissions.JournalRead)
            .WithSummary("The journal browser: newest first, filtered by dates, source document type, number prefix, an account on any line, manual only, a minimum total and free text over descriptions and numbers");
        company.MapGet("/balances", async (Guid companyId, Guid? periodId, Guid? accountId, JournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.BalancesAsync(companyId, periodId, accountId, ct)))
            .RequirePermission(AccountingPermissions.JournalRead)
            .WithSummary("The derived balance rows: account × period × transaction currency × dimension set");
        company.MapGet("/balances/verify", async (Guid companyId, JournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.VerifyBalancesAsync(companyId, ct)))
            .RequirePermission(AccountingPermissions.JournalRead)
            .WithSummary("Compares stored balances with the journal lines; differences are listed, nothing changes");
        company.MapPost("/balances/rebuild", async (Guid companyId, JournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.RebuildBalancesAsync(companyId, ct)))
            .RequirePermission(AccountingPermissions.BalanceRebuild)
            .WithSummary("Truncates the company's balances and recomputes them from the lines in one transaction (audited)");

        var profiles = accounting.MapGroup("/posting-profiles");
        profiles.MapGet("/{profileId:guid}", async (Guid profileId, ProfileService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetProfileAsync(profileId, ct), "posting_profile", profileId))
            .RequirePermission(AccountingPermissions.ProfileRead);
        profiles.MapPut("/{profileId:guid}/rules", async (Guid profileId, IReadOnlyList<PostingRuleRequest> request, ProfileService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ReplaceRulesAsync(profileId, request, ct)))
            .RequirePermission(AccountingPermissions.ProfileManage)
            .WithSummary("Replaces every rule: role + account code + optional keys (document type, posting groups, tax code, warehouse, branch, bank account, asset category, charge type)");
        profiles.MapPost("/{profileId:guid}/activate", async (Guid profileId, ProfileService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ActivateAsync(profileId, ct)))
            .RequirePermission(AccountingPermissions.ProfileManage)
            .WithSummary("Makes the profile the company's current one; the previous active version of the same code is retired");

        accounting.MapPost("/postings", async (PostJournalRequest request, PostingService service, CancellationToken ct) =>
        {
            Result<PostingResult> posted = service.IsDocumentModule(request.SourceModule) ? Reserved(request.SourceModule) : await service.PostAsync(ToRequest(request), ct);
            return ApiProblems.Created(posted, static r => $"/api/v1/accounting/journal-entries/{r.EntryId}");
        })
            .RequirePermission(AccountingPermissions.JournalPost)
            .WithSummary("Posts a balanced request through the engine: roles resolved by the company's profile, amounts converted to functional and reporting currency, period and control checks, rounding line, balances");

        var entries = accounting.MapGroup("/journal-entries");
        entries.MapGet("/{entryId:guid}", async (Guid entryId, JournalService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetEntryAsync(entryId, ct), "journal_entry", entryId))
            .RequirePermission(AccountingPermissions.JournalRead);
        entries.MapPost("/{entryId:guid}/reverse", async (Guid entryId, ReverseRequest request, PostingService service, CancellationToken ct) =>
        {
            Result<PostingResult> reversed = await service.RefuseDocumentEntryAsync(entryId, ct) is { } refused ? refused : await service.ReverseAsync(entryId, request.ReversalDate, request.Reason, cancellationToken: ct);
            return ApiProblems.Created(reversed, static r => $"/api/v1/accounting/journal-entries/{r.EntryId}");
        })
            .RequirePermission(AccountingPermissions.JournalReverse)
            .WithSummary("The mirror entry on the original date when its period is open, else on the first open period; both entries are linked");
        entries.MapPost("/{entryId:guid}/correct", async (Guid entryId, CorrectEntryRequest request, PostingService service, CancellationToken ct) =>
        {
            Result<CorrectionResult> corrected = await service.RefuseDocumentEntryAsync(entryId, ct) is { } refused ? refused
                : service.IsDocumentModule(request.Replacement.SourceModule) ? Reserved(request.Replacement.SourceModule)
                : await service.CorrectAsync(entryId, ToRequest(request.Replacement), request.Reason, ct);
            return ApiProblems.Created(corrected, static r => $"/api/v1/accounting/journal-entries/{r.Replacement.EntryId}");
        })
            .RequirePermission(AccountingPermissions.JournalReverse)
            .WithSummary("Reverses the entry into the first open period and posts the replacement there (or on its own later date); the replacement is linked to the original as its correction");

        return accounting;
    }

    private static Error Reserved(string module) =>
        Error.Validation("posting.source_module_reserved", $"Entries of '{module}' are posted by its documents, not through the journal-entry API.").WithWhy(("sourceModule", module));

    private static PostingRequest ToRequest(PostJournalRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var lines = (r.Lines ?? []).Select(l => new PostingLine(
            l.AccountRole,
            l.Amount,
            new PostingKeys(l.DocumentType, l.ItemPostingGroupId, l.PartnerPostingGroupId, l.TaxCodeId, l.WarehouseId, l.BranchId, l.BankAccountId, l.AssetCategoryId, l.ChargeTypeId),
            l.AccountId,
            l.Dimensions,
            l.PartnerId,
            l.SubledgerType,
            l.SubledgerRef,
            l.TaxCodeId,
            l.TaxBase,
            l.Description is null ? null : new LocalizedText(l.Description),
            l.DueDate)).ToList();
        return new PostingRequest(new CompanyId(r.CompanyId), r.SourceModule, r.SourceDocumentType, r.SourceDocumentId, r.PostingDate, r.Currency, lines, r.SourceDocumentNumber, r.DocumentDate,
            r.Description is null ? null : new LocalizedText(r.Description), r.BranchId is { } b ? new BranchId(b) : null, r.RateType, r.RateOverride, r.RateOverrideReason, r.IdempotencyKey,
            r.IsManual, r.IsOpeningEntry, r.IsClosingEntry, r.AutoReverseOn);
    }
}
