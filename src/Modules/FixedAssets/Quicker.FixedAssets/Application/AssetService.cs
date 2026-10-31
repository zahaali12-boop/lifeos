using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.FixedAssets.Contracts;
using Quicker.FixedAssets.Domain;
using Quicker.FixedAssets.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Integrity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Receivables.Contracts;
using Quicker.Tax.Contracts;

namespace Quicker.FixedAssets.Application;

/// <summary>
/// The asset register (A-175): assets drafted with their category, cost and dates, activated into the books (the
/// acquisition posted from its source, a plan per book), then added to, impaired, revalued, transferred and disposed
/// of, each as a journal through the posting engine and a transaction in the FA subledger; verified physically;
/// read as a register at a date and reconciled to the ledger.
/// </summary>
public sealed class AssetService(
    FixedAssetsDbContext db,
    ICompanyDirectory companies,
    IChartOfAccounts chart,
    IPostingService posting,
    IPostingRules postingRules,
    IPayables payables,
    IReceivables receivables,
    IPartnerDirectory partners,
    ICustomerDirectory customers,
    IBankAccountDirectory bankAccounts,
    IBankTransactions bankTransactions,
    ITaxDetermination tax,
    ITaxLedger taxLedger,
    IDimensionSets dimensionSets,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IInvariantHarness harness,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    BookService books,
    DepreciationService depreciation)
{
    public const string DocumentType = FixedAssetDocumentTypes.Asset;

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<AssetSummary>> ListAsync(Guid companyId, string? status, Guid? categoryId, string? q, CancellationToken cancellationToken)
    {
        var query = db.Assets.AsNoTracking().Include(static a => a.Books).Where(a => a.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(a => a.Status == status);
        }

        if (categoryId is { } c)
        {
            query = query.Where(a => a.CategoryId == c);
        }

        var assets = await query.OrderBy(static a => a.Number).Take(2000).ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            assets = assets.Where(a => a.Number.Contains(needle, StringComparison.OrdinalIgnoreCase) || a.Name.Values.Values.Any(v => v.Contains(needle, StringComparison.OrdinalIgnoreCase)) || (a.SerialNumber?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(static x => x.Id, cancellationToken);
        var glBook = await db.Books.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == companyId && b.PostsToGl, cancellationToken);
        return assets.Select(a => Map(a, categories.GetValueOrDefault(a.CategoryId), glBook?.Id, null, null, null, null, null)).ToList();
    }

    public async Task<AssetSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().Include(static a => a.Books).ThenInclude(static b => b.Schedule).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == asset.CategoryId, cancellationToken);
        var bookRows = await db.Books.AsNoTracking().Where(b => b.CompanyId == asset.CompanyId).ToDictionaryAsync(static b => b.Id, cancellationToken);
        var transactions = await db.Transactions.AsNoTracking().Where(t => t.AssetId == asset.Id).OrderBy(static t => t.PostingDate).ThenBy(static t => t.CreatedAt).ToListAsync(cancellationToken);
        var verifications = await db.Verifications.AsNoTracking().Where(v => v.AssetId == asset.Id).OrderByDescending(static v => v.VerifiedOn).ToListAsync(cancellationToken);
        var supplier = asset.SupplierPartnerId is { } p ? await partners.FindAsync(p, cancellationToken) : null;
        var dimensions = asset.DimensionSetId is { } set ? await dimensionSets.GetAsync(set, cancellationToken) : null;
        var glBook = bookRows.Values.SingleOrDefault(static b => b.PostsToGl);
        return Map(asset, category, glBook?.Id, bookRows, transactions, verifications, supplier?.Code, dimensions);
    }

    // ------------------------------------------------------------------ drafts

    public async Task<Result<AssetSummary>> CreateAsync(SaveAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var asset = new Asset { Id = Guid.CreateVersion7(), CompanyId = company.Id.Value, Currency = company.FunctionalCurrency.Code, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(asset, request, company, true, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "FA", "FA-{seq:6}", "never", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, request.AcquisitionDate, asset.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        asset.Number = number.Value.Text;
        var glBook = await books.EnsureGlBookAsync(company.Id.Value, cancellationToken);
        var allBooks = await db.Books.AsNoTracking().Where(b => b.CompanyId == company.Id.Value && b.IsActive).ToListAsync(cancellationToken);
        var category = await db.Categories.AsNoTracking().SingleAsync(c => c.Id == asset.CategoryId, cancellationToken);
        foreach (var book in allBooks.Count == 0 ? [glBook] : allBooks)
        {
            var overrides = request.Books?.FirstOrDefault(b => b.BookId == book.Id);
            var value = new AssetBookValue { Id = Guid.CreateVersion7(), AssetId = asset.Id, BookId = book.Id, Status = AssetStatuses.Draft, UpdatedAt = clock.UtcNow };
            var set = ApplyBookValue(value, category, asset, overrides);
            if (set.IsFailure)
            {
                return set.Error!;
            }

            asset.Books.Add(value);
        }

        db.Assets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, asset.Id, asset.Number, AuditActions.Created, After: new { asset.Number, asset.Cost, asset.AcquisitionSource }, CompanyId: asset.CompanyId), cancellationToken);
        return (await GetAsync(asset.Id, cancellationToken))!;
    }

    public async Task<Result<AssetSummary>> UpdateAsync(Guid id, SaveAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await db.Assets.Include(static a => a.Books).ThenInclude(static b => b.Schedule).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (asset.CompanyId != request.CompanyId)
        {
            return Error.Validation("asset.company_fixed", "An asset stays in its company.");
        }

        var company = (await companies.FindAsync(new CompanyId(asset.CompanyId), cancellationToken))!;
        var draft = asset.Status == AssetStatuses.Draft;
        if (!draft && (asset.CategoryId != request.CategoryId || asset.Cost != request.Cost || asset.AcquisitionDate != request.AcquisitionDate || asset.AcquisitionSource != request.AcquisitionSource || (request.InServiceDate ?? request.AcquisitionDate) != asset.InServiceDate))
        {
            return Error.Conflict("asset.not_draft", "Once an asset is active its category, cost, dates and source are fixed; use an addition, a revaluation or a transfer.").WithWhy(("status", asset.Status));
        }

        var applied = await ApplyAsync(asset, request, company, draft, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (draft)
        {
            var category = await db.Categories.AsNoTracking().SingleAsync(c => c.Id == asset.CategoryId, cancellationToken);
            foreach (var value in asset.Books)
            {
                var set = ApplyBookValue(value, category, asset, request.Books?.FirstOrDefault(b => b.BookId == value.BookId));
                if (set.IsFailure)
                {
                    return set.Error!;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, asset.Id, asset.Number, AuditActions.Updated, After: new { asset.Cost, asset.Status }, CompanyId: asset.CompanyId), cancellationToken);
        return (await GetAsync(asset.Id, cancellationToken))!;
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.Include(static a => a.Books).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (asset.Status != AssetStatuses.Draft)
        {
            return Error.Conflict("asset.not_draft", "Only a draft asset is deleted; dispose of an active one.").WithWhy(("status", asset.Status));
        }

        db.Assets.Remove(asset);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, asset.Id, asset.Number, AuditActions.Deleted, CompanyId: asset.CompanyId), cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ApplyAsync(Asset asset, SaveAssetRequest request, CompanyInfo company, bool draft, CancellationToken cancellationToken)
    {
        var name = Validation.Name(request.Name, "asset");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return Error.NotFound(CategoryService.EntityType, request.CategoryId);
        }

        if (request.ParentAssetId is { } parent && (parent == asset.Id || !await db.Assets.AnyAsync(a => a.Id == parent && a.CompanyId == request.CompanyId, cancellationToken)))
        {
            return Error.NotFound(DocumentType, parent);
        }

        if (request.Quantity <= 0m)
        {
            return Error.Validation("asset.quantity_invalid", "The quantity is above zero.");
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        Guid? dimensionSetId = asset.DimensionSetId;
        if (request.Dimensions is { Count: > 0 })
        {
            var set = await dimensionSets.GetOrCreateAsync(request.Dimensions, cancellationToken);
            if (set.IsFailure)
            {
                return set.Error!;
            }

            dimensionSetId = set.Value;
        }
        else if (request.Dimensions is { Count: 0 })
        {
            dimensionSetId = null;
        }

        if (draft)
        {
            var source = Validation.OneOf(request.AcquisitionSource, "asset.acquisition_source", [.. AcquisitionSources.All]);
            if (source.IsFailure)
            {
                return source.Error!;
            }

            if (request.Cost < 0m || !new Money(request.Cost, company.FunctionalCurrency).IsRoundedToMinorUnit)
            {
                return Error.Validation("asset.cost_invalid", $"The cost is zero or above, in {company.FunctionalCurrency.Code} to its minor unit.");
            }

            var inService = request.InServiceDate ?? request.AcquisitionDate;
            if (inService < request.AcquisitionDate)
            {
                return Error.Validation("asset.in_service_before_acquisition", "An asset enters service on or after it is acquired.");
            }

            switch (source.Value)
            {
                case AcquisitionSources.Supplier:
                    if (request.SupplierPartnerId is not { } supplierId)
                    {
                        return Error.Validation("asset.supplier_required", "An asset bought from a supplier names the supplier.");
                    }

                    var supplier = await partners.FindAsync(supplierId, cancellationToken);
                    if (supplier is null || !supplier.IsActive || !supplier.IsSupplier)
                    {
                        return Error.NotFound("supplier", supplierId);
                    }

                    if (request.AcquisitionDueDate is { } due && due < request.AcquisitionDate)
                    {
                        return Error.Validation("asset.due_before_acquisition", "The due date is on or after the acquisition date.");
                    }

                    break;
                case AcquisitionSources.Bank:
                    if (request.AcquisitionBankAccountId is not { } bankId)
                    {
                        return Error.Validation("asset.bank_account_required", "An asset paid at once names the bank or cash account.");
                    }

                    var bank = await bankAccounts.FindAsync(bankId, cancellationToken);
                    if (bank is null || bank.CompanyId != company.Id.Value)
                    {
                        return Error.NotFound("bank_account", bankId);
                    }

                    if (!string.Equals(bank.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
                    {
                        return Error.Validation("asset.bank_currency", "Assets are carried in the company's currency; pay from an account in that currency (a foreign-currency purchase goes through a supplier invoice).").WithWhy(("bankCurrency", bank.Currency));
                    }

                    break;
                case AcquisitionSources.Opening:
                    if (request.OpeningAccumulatedDepreciation < 0m || request.OpeningAccumulatedDepreciation > request.Cost)
                    {
                        return Error.Validation("asset.opening_depreciation_invalid", "Opening accumulated depreciation is between zero and the cost.");
                    }

                    if (request.OpeningDepreciatedThrough is null)
                    {
                        return Error.Validation("asset.opening_through_required", "Say the month the opening balances run through (the cut-over); the asset posts on the day after.");
                    }

                    break;
            }

            asset.CategoryId = category.Id;
            asset.AcquisitionDate = request.AcquisitionDate;
            asset.InServiceDate = inService;
            asset.Cost = request.Cost;
            asset.AcquisitionSource = source.Value;
            asset.AcquisitionBankAccountId = source.Value == AcquisitionSources.Bank ? request.AcquisitionBankAccountId : null;
            asset.AcquisitionTaxCodeId = source.Value == AcquisitionSources.Supplier ? request.AcquisitionTaxCodeId : null;
            asset.AcquisitionDueDate = source.Value == AcquisitionSources.Supplier ? request.AcquisitionDueDate : null;
            asset.OpeningAccumulatedDepreciation = source.Value == AcquisitionSources.Opening ? request.OpeningAccumulatedDepreciation : 0m;
            asset.OpeningDepreciatedThrough = source.Value == AcquisitionSources.Opening ? request.OpeningDepreciatedThrough : null;
            asset.Quantity = request.Quantity;
            asset.SupplierPartnerId = request.SupplierPartnerId;
            asset.ParentAssetId = request.ParentAssetId;
            asset.BranchId = request.BranchId;
            asset.DimensionSetId = dimensionSetId;
        }

        asset.Name = name.Value;
        asset.Description = Clean(request.Description);
        asset.Unit = Clean(request.Unit);
        asset.CustodianMembershipId = request.CustodianMembershipId;
        asset.SerialNumber = Clean(request.SerialNumber);
        asset.SupplierReference = Clean(request.SupplierReference);
        asset.WarrantyUntil = request.WarrantyUntil;
        asset.Notes = Clean(request.Notes);
        using var customJson = JsonDocument.Parse(custom.Value);
        asset.CustomFields = customJson.RootElement.Clone();
        asset.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>A book's view of the asset from the category's defaults and the request's overrides.</summary>
    private static Result ApplyBookValue(AssetBookValue value, AssetCategory category, Asset asset, AssetBookValueRequest? overrides)
    {
        var method = overrides?.Method ?? category.DefaultMethod;
        if (!DepreciationMethods.All.Contains(method, StringComparer.Ordinal))
        {
            return Error.Validation("asset.method_invalid", $"Unknown method '{method}'.").WithWhy(("allowed", DepreciationMethods.All));
        }

        var convention = overrides?.Convention ?? category.Convention;
        if (!DepreciationConventions.All.Contains(convention, StringComparer.Ordinal))
        {
            return Error.Validation("asset.convention_invalid", $"Unknown convention '{convention}'.").WithWhy(("allowed", DepreciationConventions.All));
        }

        var life = overrides?.LifeMonths ?? category.DefaultLifeMonths;
        if (life < 0 || (method is not (DepreciationMethods.None or DepreciationMethods.UnitsOfProduction) && life == 0))
        {
            return Error.Validation("asset.life_invalid", "A depreciating asset has a life in months.");
        }

        var salvage = overrides?.SalvageValue ?? RoundingPolicy.Default.Round(asset.Cost * category.DefaultSalvagePct / 100m, 2);
        if (salvage < 0m || salvage > asset.Cost)
        {
            return Error.Validation("asset.salvage_invalid", "The salvage value is between zero and the cost.");
        }

        if (method == DepreciationMethods.UnitsOfProduction && (overrides?.TotalUnits ?? value.TotalUnits) is not > 0m)
        {
            return Error.Validation("asset.total_units_required", "Units of production needs the total units the asset will produce.");
        }

        value.Method = method;
        value.LifeMonths = life;
        value.SalvageValue = salvage;
        value.RatePct = overrides?.RatePct ?? category.DefaultRatePct;
        value.Convention = convention;
        value.DepreciationStart = asset.InServiceDate;
        value.TotalUnits = overrides?.TotalUnits ?? value.TotalUnits;
        value.Cost = asset.Cost;
        return Result.Success();
    }

    // ------------------------------------------------------------------ activation and additions

    /// <summary>Posts the acquisition from its source, values the asset in every book and plans its depreciation.</summary>
    public async Task<Result<AssetSummary>> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.Include(static a => a.Books).ThenInclude(static b => b.Schedule).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (asset.Status != AssetStatuses.Draft)
        {
            return Error.Conflict("asset.not_draft", "The asset is already active.").WithWhy(("status", asset.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(asset.CompanyId), cancellationToken))!;
        var category = await db.Categories.AsNoTracking().SingleAsync(c => c.Id == asset.CategoryId, cancellationToken);
        var rules = await EnsureCategoryRulesAsync(company, category, cancellationToken);
        if (rules.IsFailure)
        {
            return rules.Error!;
        }

        var glBook = await books.EnsureGlBookAsync(company.Id.Value, cancellationToken);
        if (!asset.Books.Any(b => b.BookId == glBook.Id))
        {
            var value = new AssetBookValue { Id = Guid.CreateVersion7(), AssetId = asset.Id, BookId = glBook.Id, Status = AssetStatuses.Draft, UpdatedAt = clock.UtcNow };
            ApplyBookValue(value, category, asset, null);
            asset.Books.Add(value);
        }

        // An asset held before go-live posts on the cut-over day (the day after its opening balances run through), not on its historical acquisition date.
        var postingDate = asset.AcquisitionSource == AcquisitionSources.Opening ? asset.OpeningDepreciatedThroughOrAcquisition().AddDays(1) : asset.AcquisitionDate;
        var transactionId = Guid.CreateVersion7();
        var credit = await CreditSideAsync(asset, company, asset.Cost, postingDate, asset.AcquisitionSource, asset.SupplierPartnerId, asset.AcquisitionBankAccountId, asset.AcquisitionTaxCodeId, asset.AcquisitionDueDate, asset.OpeningAccumulatedDepreciation, transactionId, asset.SupplierReference, cancellationToken);
        if (credit.IsFailure)
        {
            return credit.Error!;
        }

        var (lines, costBasis) = credit.Value;
        var keys = Keys(asset);
        var dimensions = await DimensionsAsync(asset, cancellationToken);
        var description = LocalizedText.Bilingual($"Asset {asset.Number} acquired – {asset.Name.Values.GetValueOrDefault("en", asset.Number)}", $"اقتناء الأصل {asset.Number} – {asset.Name.Values.GetValueOrDefault("ar", asset.Number)}");
        lines.Insert(0, new PostingLine(AccountRoles.FaCost, costBasis, keys, Dimensions: dimensions, PartnerId: asset.SupplierPartnerId, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "fixed_assets", DocumentType, asset.Id, postingDate, company.FunctionalCurrency.Code, lines, asset.Number, asset.AcquisitionDate, description, IdempotencyKey: $"fa_asset:{asset.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var opened = await AfterCreditAsync(asset, company, costBasis, postingDate, asset.AcquisitionSource, asset.SupplierPartnerId, asset.AcquisitionBankAccountId, asset.AcquisitionDueDate, transactionId, asset.Number, asset.SupplierReference, posted.Value, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        asset.Cost = costBasis;
        asset.AcquisitionEntryId = posted.Value.EntryId;
        asset.Status = AssetStatuses.Active;
        asset.UpdatedAt = clock.UtcNow;
        foreach (var value in asset.Books)
        {
            var book = await db.Books.AsNoTracking().SingleAsync(b => b.Id == value.BookId, cancellationToken);
            value.Cost = costBasis;
            value.Status = AssetStatuses.Active;
            if (asset.AcquisitionSource == AcquisitionSources.Opening && asset.OpeningAccumulatedDepreciation > 0m)
            {
                value.AccumulatedDepreciation = asset.OpeningAccumulatedDepreciation;
                value.MonthsConsumed = ConsumedThrough(value, asset.OpeningDepreciatedThroughOrAcquisition());
                value.DepreciatedThrough = asset.OpeningDepreciatedThroughOrAcquisition();
            }

            db.Transactions.Add(new AssetTransaction
            {
                Id = value.BookId == glBook.Id ? transactionId : Guid.CreateVersion7(),
                CompanyId = asset.CompanyId,
                AssetId = asset.Id,
                BookId = value.BookId,
                PostingDate = postingDate,
                Kind = AssetTransactionKinds.Acquisition,
                CostDelta = costBasis,
                AccumulatedDepreciationDelta = value.AccumulatedDepreciation,
                JournalEntryId = book.PostsToGl ? posted.Value.EntryId : null,
                Reference = asset.SupplierReference ?? asset.Number,
                Detail = $"{{\"source\":\"{asset.AcquisitionSource}\"}}",
                CreatedAt = clock.UtcNow,
            });
            await depreciation.RegenerateAsync(value, company.FunctionalCurrency.MinorUnits, cancellationToken);
        }

        if (asset.Books.Any(b => b.BookId == glBook.Id && b.Status == AssetStatuses.FullyDepreciated))
        {
            asset.Status = AssetStatuses.FullyDepreciated;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, asset.Id, asset.Number, AuditActions.Posted, After: new { asset.Status, cost = costBasis, journalEntryId = posted.Value.EntryId, source = asset.AcquisitionSource }, CompanyId: asset.CompanyId), cancellationToken);
        return (await GetAsync(asset.Id, cancellationToken))!;
    }

    /// <summary>Capital added to an active asset (an improvement): Dr cost / Cr the source; the plan continues over the remaining life.</summary>
    public async Task<Result<AssetSummary>> AddAsync(Guid id, AdditionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await LoadActiveAsync(id, cancellationToken);
        if (asset.IsFailure)
        {
            return asset.Error!;
        }

        var a = asset.Value;
        var company = (await companies.FindAsync(new CompanyId(a.CompanyId), cancellationToken))!;
        if (request.Amount <= 0m || !new Money(request.Amount, company.FunctionalCurrency).IsRoundedToMinorUnit)
        {
            return Error.Validation("asset.amount_invalid", "The addition is above zero, to the minor unit.");
        }

        if (request.Source == AcquisitionSources.Opening || !AcquisitionSources.All.Contains(request.Source, StringComparer.Ordinal))
        {
            return Error.Validation("asset.source_invalid", "An addition comes from a supplier, a bank account or the clearing account.");
        }

        var before = await BeforeChangeAsync(a, request.Date, cancellationToken);
        if (before.IsFailure)
        {
            return before.Error!;
        }

        var transactionId = Guid.CreateVersion7();
        var credit = await CreditSideAsync(a, company, request.Amount, request.Date, request.Source, request.SupplierPartnerId ?? a.SupplierPartnerId, request.BankAccountId, request.TaxCodeId, request.DueDate, 0m, transactionId, request.Reference, cancellationToken);
        if (credit.IsFailure)
        {
            return credit.Error!;
        }

        var (lines, costBasis) = credit.Value;
        var what = request.Description ?? "addition";
        var description = LocalizedText.Bilingual($"Asset {a.Number}: {what}", $"الأصل {a.Number}: {what}");
        lines.Insert(0, new PostingLine(AccountRoles.FaCost, costBasis, Keys(a), Dimensions: await DimensionsAsync(a, cancellationToken), PartnerId: request.Source == AcquisitionSources.Supplier ? request.SupplierPartnerId ?? a.SupplierPartnerId : null, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "fixed_assets", DocumentType, a.Id, request.Date, company.FunctionalCurrency.Code, lines, a.Number, request.Date, description, IdempotencyKey: $"fa_addition:{transactionId}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var opened = await AfterCreditAsync(a, company, costBasis, request.Date, request.Source, request.SupplierPartnerId ?? a.SupplierPartnerId, request.BankAccountId, request.DueDate, transactionId, $"{a.Number}/{transactionId.ToString("N")[^4..].ToUpperInvariant()}", request.Reference, posted.Value, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        a.Cost += costBasis;
        a.UpdatedAt = clock.UtcNow;
        var glBookId = await GlBookIdAsync(a.CompanyId, cancellationToken);
        foreach (var value in a.Books.Where(static v => v.Status is AssetStatuses.Active or AssetStatuses.FullyDepreciated))
        {
            value.Cost += costBasis;
            if (value.Status == AssetStatuses.FullyDepreciated)
            {
                value.Status = AssetStatuses.Active;
            }

            db.Transactions.Add(Transaction(a, value, value.BookId == glBookId ? transactionId : Guid.CreateVersion7(), request.Date, AssetTransactionKinds.Addition, value.BookId == glBookId ? posted.Value.EntryId : null, request.Reference, costDelta: costBasis, detail: $"{{\"source\":\"{request.Source}\"}}"));
            await depreciation.RegenerateAsync(value, company.FunctionalCurrency.MinorUnits, cancellationToken);
        }

        if (a.Status == AssetStatuses.FullyDepreciated)
        {
            a.Status = AssetStatuses.Active;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, a.Id, a.Number, AuditActions.Posted, After: new { addition = costBasis, request.Date, journalEntryId = posted.Value.EntryId }, CompanyId: a.CompanyId), cancellationToken);
        return (await GetAsync(a.Id, cancellationToken))!;
    }

    // ------------------------------------------------------------------ impairment and revaluation

    /// <summary>Writes the asset down: the revaluation surplus it has takes the hit first, the rest is a loss; depreciation continues on the lower amount.</summary>
    public async Task<Result<AssetSummary>> ImpairAsync(Guid id, ImpairmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await LoadActiveAsync(id, cancellationToken);
        if (asset.IsFailure)
        {
            return asset.Error!;
        }

        var a = asset.Value;
        var company = (await companies.FindAsync(new CompanyId(a.CompanyId), cancellationToken))!;
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("asset.reason_required", "An impairment names its reason.");
        }

        var before = await BeforeChangeAsync(a, request.Date, cancellationToken);
        if (before.IsFailure)
        {
            return before.Error!;
        }

        var glBookId = await GlBookIdAsync(a.CompanyId, cancellationToken);
        var gl = a.Books.Single(v => v.BookId == glBookId);
        if (request.Amount <= 0m || request.Amount > gl.NetBookValue || !new Money(request.Amount, company.FunctionalCurrency).IsRoundedToMinorUnit)
        {
            return Error.Validation("asset.impairment_invalid", "An impairment is above zero and at most the net book value.").WithWhy(("netBookValue", gl.NetBookValue));
        }

        var transactionId = Guid.CreateVersion7();
        var fromSurplus = Math.Min(gl.RevaluationSurplus, request.Amount);
        var loss = request.Amount - fromSurplus;
        var keys = Keys(a);
        var dimensions = await DimensionsAsync(a, cancellationToken);
        var description = LocalizedText.Bilingual($"Impairment of {a.Number} – {request.Reason}", $"انخفاض قيمة {a.Number} – {request.Reason}");
        var lines = new List<PostingLine>();
        if (fromSurplus > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.RevaluationSurplus, fromSurplus, keys, Dimensions: dimensions, Description: description));
        }

        if (loss > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.ImpairmentLoss, loss, keys, Dimensions: dimensions, Description: description));
        }

        lines.Add(new PostingLine(AccountRoles.FaAccImpairment, -request.Amount, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "fixed_assets", DocumentType, a.Id, request.Date, company.FunctionalCurrency.Code, lines, a.Number, request.Date, description, IdempotencyKey: $"fa_impairment:{transactionId}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        foreach (var value in a.Books.Where(static v => v.Status is AssetStatuses.Active or AssetStatuses.FullyDepreciated))
        {
            var isGl = value.BookId == glBookId;
            var amount = Math.Min(request.Amount, value.NetBookValue);
            var surplusUsed = Math.Min(value.RevaluationSurplus, amount);
            value.AccumulatedImpairment += amount;
            value.RevaluationSurplus -= surplusUsed;
            value.ImpairmentRecognised += amount - surplusUsed;
            db.Transactions.Add(Transaction(a, value, isGl ? transactionId : Guid.CreateVersion7(), request.Date, AssetTransactionKinds.Impairment, isGl ? posted.Value.EntryId : null, request.Reason, impairmentDelta: amount, surplusDelta: -surplusUsed));
            await depreciation.RegenerateAsync(value, company.FunctionalCurrency.MinorUnits, cancellationToken);
        }

        a.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, a.Id, a.Number, AuditActions.Posted, After: new { impairment = request.Amount, request.Date, journalEntryId = posted.Value.EntryId }, Reason: request.Reason, CompanyId: a.CompanyId), cancellationToken);
        return (await GetAsync(a.Id, cancellationToken))!;
    }

    /// <summary>
    /// Restates the asset at a fair value (IAS 16 revaluation model, elimination approach): accumulated depreciation and
    /// impairment are eliminated against cost, then the difference to fair value reverses earlier impairment losses
    /// through profit or loss first and goes to the revaluation surplus beyond that; a fall uses the surplus first and
    /// is a loss beyond it. The plan restarts on the new amount over the life that is left (or a new one).
    /// </summary>
    public async Task<Result<AssetSummary>> RevalueAsync(Guid id, RevaluationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await LoadActiveAsync(id, cancellationToken);
        if (asset.IsFailure)
        {
            return asset.Error!;
        }

        var a = asset.Value;
        var company = (await companies.FindAsync(new CompanyId(a.CompanyId), cancellationToken))!;
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("asset.reason_required", "A revaluation names its reason (the valuation it rests on).");
        }

        if (request.FairValue < 0m || !new Money(request.FairValue, company.FunctionalCurrency).IsRoundedToMinorUnit)
        {
            return Error.Validation("asset.fair_value_invalid", "The fair value is zero or above, to the minor unit.");
        }

        if (request.NewLifeMonths is { } newLife && newLife <= 0)
        {
            return Error.Validation("asset.life_invalid", "The remaining life is at least one month.");
        }

        var before = await BeforeChangeAsync(a, request.Date, cancellationToken);
        if (before.IsFailure)
        {
            return before.Error!;
        }

        var glBookId = await GlBookIdAsync(a.CompanyId, cancellationToken);
        var gl = a.Books.Single(v => v.BookId == glBookId);
        var transactionId = Guid.CreateVersion7();
        var keys = Keys(a);
        var dimensions = await DimensionsAsync(a, cancellationToken);
        var description = LocalizedText.Bilingual($"Revaluation of {a.Number} to {request.FairValue.ToString("0.##", CultureInfo.InvariantCulture)} – {request.Reason}", $"إعادة تقييم {a.Number} إلى {request.FairValue.ToString("0.##", CultureInfo.InvariantCulture)} – {request.Reason}");
        var lines = new List<PostingLine>();
        var eliminated = gl.AccumulatedDepreciation + gl.AccumulatedImpairment;
        if (gl.AccumulatedDepreciation > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.FaAccDep, gl.AccumulatedDepreciation, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        }

        if (gl.AccumulatedImpairment > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.FaAccImpairment, gl.AccumulatedImpairment, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        }

        var delta = request.FairValue - gl.NetBookValue;
        var reversal = delta > 0m ? Math.Min(delta, gl.ImpairmentRecognised) : 0m;
        var toSurplus = delta > 0m ? delta - reversal : 0m;
        var fromSurplus = delta < 0m ? Math.Min(-delta, gl.RevaluationSurplus) : 0m;
        var loss = delta < 0m ? -delta - fromSurplus : 0m;
        var costChange = delta - eliminated;
        if (costChange != 0m)
        {
            lines.Add(new PostingLine(AccountRoles.FaCost, costChange, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        }

        if (reversal > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.ImpairmentLoss, -reversal, keys, Dimensions: dimensions, Description: description));
        }

        if (toSurplus > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.RevaluationSurplus, -toSurplus, keys, Dimensions: dimensions, Description: description));
        }

        if (fromSurplus > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.RevaluationSurplus, fromSurplus, keys, Dimensions: dimensions, Description: description));
        }

        if (loss > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.ImpairmentLoss, loss, keys, Dimensions: dimensions, Description: description));
        }

        if (lines.Count == 0)
        {
            return Error.Validation("asset.revaluation_no_change", "The fair value equals the carrying amount; nothing to post.");
        }

        var posted = await posting.PostAsync(new PostingRequest(company.Id, "fixed_assets", DocumentType, a.Id, request.Date, company.FunctionalCurrency.Code, lines, a.Number, request.Date, description, IdempotencyKey: $"fa_revaluation:{transactionId}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        foreach (var value in a.Books.Where(static v => v.Status is AssetStatuses.Active or AssetStatuses.FullyDepreciated))
        {
            var isGl = value.BookId == glBookId;
            var bookDelta = request.FairValue - value.NetBookValue;
            var bookReversal = bookDelta > 0m ? Math.Min(bookDelta, value.ImpairmentRecognised) : 0m;
            var bookToSurplus = bookDelta > 0m ? bookDelta - bookReversal : 0m;
            var bookFromSurplus = bookDelta < 0m ? Math.Min(-bookDelta, value.RevaluationSurplus) : 0m;
            var bookLoss = bookDelta < 0m ? -bookDelta - bookFromSurplus : 0m;
            db.Transactions.Add(Transaction(a, value, isGl ? transactionId : Guid.CreateVersion7(), request.Date, AssetTransactionKinds.Revaluation, isGl ? posted.Value.EntryId : null, request.Reason,
                costDelta: request.FairValue - value.Cost, accumulatedDepreciationDelta: -value.AccumulatedDepreciation, impairmentDelta: -value.AccumulatedImpairment, surplusDelta: bookToSurplus - bookFromSurplus,
                detail: $"{{\"fairValue\":{request.FairValue.ToString(CultureInfo.InvariantCulture)},\"reversedImpairment\":{bookReversal.ToString(CultureInfo.InvariantCulture)},\"loss\":{bookLoss.ToString(CultureInfo.InvariantCulture)}}}"));
            value.Cost = request.FairValue;
            value.AccumulatedDepreciation = 0m;
            value.AccumulatedImpairment = 0m;
            value.RevaluationSurplus += bookToSurplus - bookFromSurplus;
            value.ImpairmentRecognised += bookLoss - bookReversal;
            if (request.NewLifeMonths is { } months)
            {
                value.LifeMonths = WholeMonths(value.MonthsConsumed) + months;
            }

            if (value.Status == AssetStatuses.FullyDepreciated && value.NetBookValue - value.SalvageValue > 0m)
            {
                value.Status = AssetStatuses.Active;
            }

            await depreciation.RegenerateAsync(value, company.FunctionalCurrency.MinorUnits, cancellationToken);
        }

        a.Cost = request.FairValue;
        if (a.Status == AssetStatuses.FullyDepreciated && gl.Status == AssetStatuses.Active)
        {
            a.Status = AssetStatuses.Active;
        }

        a.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, a.Id, a.Number, AuditActions.Posted, After: new { fairValue = request.FairValue, request.Date, surplus = toSurplus - fromSurplus, reversedImpairment = reversal, loss, journalEntryId = posted.Value.EntryId }, Reason: request.Reason, CompanyId: a.CompanyId), cancellationToken);
        return (await GetAsync(a.Id, cancellationToken))!;
    }

    // ------------------------------------------------------------------ transfer, disposal

    /// <summary>Moves the asset to another branch, location or custodian; a change of branch or dimensions moves its balances in the ledger too.</summary>
    public async Task<Result<AssetSummary>> TransferAsync(Guid id, TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await db.Assets.Include(static a => a.Books).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (asset.Status == AssetStatuses.Disposed)
        {
            return Error.Conflict("asset.disposed", "A disposed asset is not transferred.");
        }

        Guid? newSet = asset.DimensionSetId;
        if (request.Dimensions is { Count: > 0 })
        {
            var set = await dimensionSets.GetOrCreateAsync(request.Dimensions, cancellationToken);
            if (set.IsFailure)
            {
                return set.Error!;
            }

            newSet = set.Value;
        }
        else if (request.Dimensions is { Count: 0 })
        {
            newSet = null;
        }

        var newBranch = request.BranchId ?? asset.BranchId;
        var moves = asset.Status != AssetStatuses.Draft && (newSet != asset.DimensionSetId || newBranch != asset.BranchId);
        Guid? entryId = null;
        var transactionId = Guid.CreateVersion7();
        if (moves)
        {
            var company = (await companies.FindAsync(new CompanyId(asset.CompanyId), cancellationToken))!;
            var glBookId = await GlBookIdAsync(asset.CompanyId, cancellationToken);
            var gl = asset.Books.Single(v => v.BookId == glBookId);
            var oldKeys = Keys(asset);
            var newKeys = new PostingKeys(DocumentType, AssetCategoryId: asset.CategoryId, BranchId: newBranch);
            var oldDims = await DimensionsAsync(asset, cancellationToken);
            var newDims = newSet is { } s ? await dimensionSets.GetAsync(s, cancellationToken) : null;
            var description = LocalizedText.Bilingual($"Transfer of {asset.Number}{(request.Reason is null ? "" : " – " + request.Reason)}", $"نقل {asset.Number}{(request.Reason is null ? "" : " – " + request.Reason)}");
            var lines = new List<PostingLine>();
            void Move(string role, decimal balance)
            {
                if (balance == 0m)
                {
                    return;
                }

                lines.Add(new PostingLine(role, balance, newKeys, Dimensions: newDims, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
                lines.Add(new PostingLine(role, -balance, oldKeys, Dimensions: oldDims, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
            }

            Move(AccountRoles.FaCost, gl.Cost);
            Move(AccountRoles.FaAccDep, -gl.AccumulatedDepreciation);
            Move(AccountRoles.FaAccImpairment, -gl.AccumulatedImpairment);
            if (lines.Count > 0)
            {
                var posted = await posting.PostAsync(new PostingRequest(company.Id, "fixed_assets", DocumentType, asset.Id, request.Date, company.FunctionalCurrency.Code, lines, asset.Number, request.Date, description, IdempotencyKey: $"fa_transfer:{transactionId}"), cancellationToken);
                if (posted.IsFailure)
                {
                    return posted.Error!;
                }

                entryId = posted.Value.EntryId;
            }

            db.Transactions.Add(Transaction(asset, gl, transactionId, request.Date, AssetTransactionKinds.Transfer, entryId, request.Reason,
                detail: $"{{\"fromBranch\":{(asset.BranchId is { } fb ? "\"" + fb + "\"" : "null")},\"toBranch\":{(newBranch is { } tb ? "\"" + tb + "\"" : "null")},\"fromDimensionSet\":{(asset.DimensionSetId is { } fd ? "\"" + fd + "\"" : "null")},\"toDimensionSet\":{(newSet is { } td ? "\"" + td + "\"" : "null")}}}"));
        }

        var beforeState = new { asset.BranchId, asset.DimensionSetId, asset.CustodianMembershipId };
        asset.BranchId = newBranch;
        asset.DimensionSetId = newSet;
        asset.CustodianMembershipId = request.CustodianMembershipId ?? asset.CustodianMembershipId;
        asset.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, asset.Id, asset.Number, AuditActions.Updated, Before: beforeState, After: new { asset.BranchId, asset.DimensionSetId, asset.CustodianMembershipId, journalEntryId = entryId }, Reason: request.Reason, CompanyId: asset.CompanyId), cancellationToken);
        return (await GetAsync(asset.Id, cancellationToken))!;
    }

    /// <summary>
    /// Sells or scraps the asset, or part of it: depreciation is caught up to the day it leaves by the convention, then
    /// the cost, accumulated depreciation and impairment leave the books, the proceeds come in (to a bank account or as
    /// a receivable on a customer) and the difference is the gain or loss; any revaluation surplus moves to retained earnings.
    /// </summary>
    public async Task<Result<AssetSummary>> DisposeAsync(Guid id, DisposalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await LoadActiveAsync(id, cancellationToken);
        if (asset.IsFailure)
        {
            return asset.Error!;
        }

        var a = asset.Value;
        var company = (await companies.FindAsync(new CompanyId(a.CompanyId), cancellationToken))!;
        var kind = Validation.OneOf(request.Kind, "asset.disposal_kind", [.. DisposalKinds.All]);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        var source = Validation.OneOf(request.ProceedsSource, "asset.proceeds_source", [.. ProceedsSources.All]);
        if (source.IsFailure)
        {
            return source.Error!;
        }

        if (request.Proceeds < 0m || !new Money(request.Proceeds, company.FunctionalCurrency).IsRoundedToMinorUnit)
        {
            return Error.Validation("asset.proceeds_invalid", "Proceeds are zero or above, to the minor unit.");
        }

        if (request.Proceeds > 0m && source.Value == ProceedsSources.None)
        {
            return Error.Validation("asset.proceeds_source_required", "Say where the proceeds land: a bank account or a customer.");
        }

        if (request.Date < a.AcquisitionDate)
        {
            return Error.Validation("asset.disposal_before_acquisition", "An asset is disposed of after it was acquired.");
        }

        var before = await BeforeChangeAsync(a, request.Date, cancellationToken);
        if (before.IsFailure)
        {
            return before.Error!;
        }

        var partial = request.Quantity is { } q && q > 0m && q < a.Quantity;
        if (request.Quantity is { } quantity && (quantity <= 0m || quantity > a.Quantity))
        {
            return Error.Validation("asset.disposal_quantity_invalid", "The quantity disposed of is above zero and at most the asset's quantity.").WithWhy(("quantity", a.Quantity));
        }

        var portion = partial ? request.Quantity!.Value / a.Quantity : 1m;
        var minor = company.FunctionalCurrency.MinorUnits;

        // Depreciation to the day it leaves, in every book, by the convention; nothing due is fine.
        var glBook = await db.Books.AsNoTracking().SingleAsync(b => b.CompanyId == a.CompanyId && b.PostsToGl, cancellationToken);
        foreach (var value in a.Books.Where(static v => v.Status == AssetStatuses.Active).ToList())
        {
            var book = value.BookId == glBook.Id ? glBook : await db.Books.AsNoTracking().SingleAsync(b => b.Id == value.BookId, cancellationToken);
            var caught = await depreciation.PostAsync(company, book, request.Date, request.Date, a.Id, Depreciation.LeavingMonthWeight(value.Convention, request.Date), cancellationToken);
            if (caught.IsFailure && caught.Error!.Code != "depreciation.nothing_due")
            {
                return caught.Error!;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var entry in db.ChangeTracker.Entries<AssetBookValue>().ToList())
        {
            await entry.ReloadAsync(cancellationToken);
        }

        var gl = a.Books.Single(v => v.BookId == glBook.Id);
        var costOut = partial ? RoundingPolicy.Default.Round(gl.Cost * portion, minor) : gl.Cost;
        var depOut = partial ? RoundingPolicy.Default.Round(gl.AccumulatedDepreciation * portion, minor) : gl.AccumulatedDepreciation;
        var impOut = partial ? RoundingPolicy.Default.Round(gl.AccumulatedImpairment * portion, minor) : gl.AccumulatedImpairment;
        var surplusOut = partial ? RoundingPolicy.Default.Round(gl.RevaluationSurplus * portion, minor) : gl.RevaluationSurplus;
        var netOut = costOut - depOut - impOut;
        var gainLoss = request.Proceeds - netOut;
        var transactionId = Guid.CreateVersion7();
        var proceedsRef = Guid.CreateVersion7();
        var keys = Keys(a);
        var dimensions = await DimensionsAsync(a, cancellationToken);
        var description = LocalizedText.Bilingual($"{(kind.Value == DisposalKinds.Sale ? "Sale" : "Scrapping")} of {a.Number}{(partial ? " (part)" : "")}{(request.Reason is null ? "" : " – " + request.Reason)}", $"{(kind.Value == DisposalKinds.Sale ? "بيع" : "إتلاف")} {a.Number}{(partial ? " (جزء)" : "")}{(request.Reason is null ? "" : " – " + request.Reason)}");
        var lines = new List<PostingLine>();
        BankAccountInfo? bank = null;
        CustomerTermsInfo? customer = null;
        if (request.Proceeds > 0m)
        {
            if (source.Value == ProceedsSources.Bank)
            {
                bank = request.BankAccountId is { } bankId ? await bankAccounts.FindAsync(bankId, cancellationToken) : null;
                if (bank is null || bank.CompanyId != a.CompanyId || !string.Equals(bank.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
                {
                    return Error.NotFound("bank_account", request.BankAccountId ?? Guid.Empty);
                }

                lines.Add(new PostingLine(RoleOf(bank.Kind), request.Proceeds, new PostingKeys(FixedAssetDocumentTypes.Disposal, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: proceedsRef, Description: description));
            }
            else
            {
                if (request.CustomerPartnerId is not { } customerId)
                {
                    return Error.Validation("asset.customer_required", "A sale on credit names the customer.");
                }

                var partner = await partners.FindAsync(customerId, cancellationToken);
                if (partner is null || !partner.IsActive || !partner.IsCustomer)
                {
                    return Error.NotFound("customer", customerId);
                }

                customer = await customers.FindCustomerAsync(a.CompanyId, customerId, cancellationToken);
                lines.Add(new PostingLine(AccountRoles.AR, request.Proceeds, new PostingKeys(FixedAssetDocumentTypes.Disposal, PartnerPostingGroupId: customer?.PostingGroupId), PartnerId: customerId, SubledgerType: SubledgerTypes.Receivables, SubledgerRef: proceedsRef, DueDate: request.DueDate ?? request.Date, Description: description));
            }
        }

        if (depOut != 0m)
        {
            lines.Add(new PostingLine(AccountRoles.FaAccDep, depOut, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        }

        if (impOut != 0m)
        {
            lines.Add(new PostingLine(AccountRoles.FaAccImpairment, impOut, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        }

        if (costOut != 0m)
        {
            lines.Add(new PostingLine(AccountRoles.FaCost, -costOut, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId, Description: description));
        }

        if (gainLoss != 0m)
        {
            lines.Add(new PostingLine(AccountRoles.GainLossOnDisposal, -gainLoss, keys, Dimensions: dimensions, Description: description));
        }

        if (surplusOut != 0m)
        {
            lines.Add(new PostingLine(AccountRoles.RevaluationSurplus, surplusOut, keys, Dimensions: dimensions, Description: description));
            lines.Add(new PostingLine(AccountRoles.RetainedEarnings, -surplusOut, keys, Description: description));
        }

        if (lines.Count == 0)
        {
            return Error.Validation("asset.nothing_to_dispose", "The asset carries no value and no proceeds; nothing to post.");
        }

        var posted = await posting.PostAsync(new PostingRequest(company.Id, "fixed_assets", FixedAssetDocumentTypes.Disposal, transactionId, request.Date, company.FunctionalCurrency.Code, lines, a.Number, request.Date, description, IdempotencyKey: $"fa_disposal:{transactionId}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        if (bank is not null)
        {
            await bankTransactions.RecordAsync(new NewBankTransaction(proceedsRef, bank.Id, a.CompanyId, request.Date, "receipt", request.Proceeds, request.Proceeds, a.Number, FixedAssetDocumentTypes.Disposal, transactionId, posted.Value.EntryId), cancellationToken);
        }
        else if (customer is not null || (request.Proceeds > 0m && source.Value == ProceedsSources.Customer))
        {
            await receivables.OpenAsync(new NewReceivable(a.CompanyId, request.CustomerPartnerId!.Value, ReceivableKinds.Invoice, FixedAssetDocumentTypes.Disposal, transactionId, $"{a.Number}/D", 1, request.Reason, request.Date, request.Date, request.DueDate ?? request.Date, null, 0m, company.FunctionalCurrency.Code, request.Proceeds, request.Proceeds, 1m, posted.Value.EntryId, a.BranchId, a.DimensionSetId), cancellationToken);
        }

        foreach (var value in a.Books.Where(static v => v.Status is AssetStatuses.Active or AssetStatuses.FullyDepreciated))
        {
            var isGl = value.BookId == glBook.Id;
            var bCost = partial ? RoundingPolicy.Default.Round(value.Cost * portion, minor) : value.Cost;
            var bDep = partial ? RoundingPolicy.Default.Round(value.AccumulatedDepreciation * portion, minor) : value.AccumulatedDepreciation;
            var bImp = partial ? RoundingPolicy.Default.Round(value.AccumulatedImpairment * portion, minor) : value.AccumulatedImpairment;
            var bSurplus = partial ? RoundingPolicy.Default.Round(value.RevaluationSurplus * portion, minor) : value.RevaluationSurplus;
            db.Transactions.Add(Transaction(a, value, isGl ? transactionId : Guid.CreateVersion7(), request.Date, AssetTransactionKinds.Disposal, isGl ? posted.Value.EntryId : null, request.Reason,
                costDelta: -bCost, accumulatedDepreciationDelta: -bDep, impairmentDelta: -bImp, surplusDelta: -bSurplus, proceeds: isGl ? request.Proceeds : RoundingPolicy.Default.Round(request.Proceeds, minor), gainLoss: request.Proceeds - (bCost - bDep - bImp),
                detail: $"{{\"kind\":\"{kind.Value}\",\"portion\":{portion.ToString(CultureInfo.InvariantCulture)},\"proceedsSource\":\"{source.Value}\"}}"));
            value.Cost -= bCost;
            value.AccumulatedDepreciation -= bDep;
            value.AccumulatedImpairment -= bImp;
            value.RevaluationSurplus -= bSurplus;
            if (partial)
            {
                value.SalvageValue = RoundingPolicy.Default.Round(value.SalvageValue * (1m - portion), minor);
                await depreciation.RegenerateAsync(value, minor, cancellationToken);
            }
            else
            {
                value.Status = AssetStatuses.Disposed;
                foreach (var line in await db.Schedule.Where(s => s.AssetBookId == value.Id && s.Status == ScheduleStatuses.Planned).ToListAsync(cancellationToken))
                {
                    line.Status = ScheduleStatuses.Skipped;
                }
            }

            value.UpdatedAt = clock.UtcNow;
        }

        if (partial)
        {
            a.Quantity -= request.Quantity!.Value;
            a.Cost -= costOut;
        }
        else
        {
            a.Status = AssetStatuses.Disposed;
            a.DisposedOn = request.Date;
            a.DisposalKind = kind.Value;
        }

        a.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, a.Id, a.Number, AuditActions.Posted, After: new { disposal = kind.Value, request.Date, proceeds = request.Proceeds, gainLoss, partial, journalEntryId = posted.Value.EntryId }, Reason: request.Reason, CompanyId: a.CompanyId), cancellationToken);
        return (await GetAsync(a.Id, cancellationToken))!;
    }

    // ------------------------------------------------------------------ verification, usage, estimates

    public async Task<Result<AssetSummary>> VerifyAsync(Guid id, VerificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        var condition = Validation.OneOf(request.Condition, "asset.condition", "good", "damaged", "missing");
        if (condition.IsFailure)
        {
            return condition.Error!;
        }

        db.Verifications.Add(new AssetVerification { Id = Guid.CreateVersion7(), AssetId = asset.Id, VerifiedOn = request.VerifiedOn, VerifiedBy = principal.Principal?.MembershipId.Value, Condition = condition.Value, LocationNote = Clean(request.LocationNote), Note = Clean(request.Note), CreatedAt = clock.UtcNow });
        asset.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, asset.Id, asset.Number, AuditActions.Updated, After: new { verifiedOn = request.VerifiedOn, condition = condition.Value, request.LocationNote }, CompanyId: asset.CompanyId), cancellationToken);
        return (await GetAsync(asset.Id, cancellationToken))!;
    }

    /// <summary>Units produced in a month, for a book on the units-of-production method; the plan follows the usage.</summary>
    public async Task<Result<AssetSummary>> RecordUsageAsync(Guid id, UsageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await LoadActiveAsync(id, cancellationToken);
        if (asset.IsFailure)
        {
            return asset.Error!;
        }

        var a = asset.Value;
        var value = a.Books.SingleOrDefault(v => v.BookId == request.BookId);
        if (value is null)
        {
            return Error.NotFound(BookService.EntityType, request.BookId);
        }

        if (value.Method != DepreciationMethods.UnitsOfProduction)
        {
            return Error.Conflict("asset.not_units_method", "Usage is recorded for a book on the units-of-production method.");
        }

        if (request.Units < 0m)
        {
            return Error.Validation("asset.units_invalid", "Units are zero or above.");
        }

        var month = new DateOnly(request.Month.Year, request.Month.Month, 1);
        if (value.Schedule.Any(s => s.PeriodStart == month && s.Status == ScheduleStatuses.Posted))
        {
            return Error.Conflict("asset.month_posted", "That month's depreciation is posted; reverse the run to change its usage.");
        }

        var usage = await db.Usage.SingleOrDefaultAsync(u => u.AssetBookId == value.Id && u.PeriodStart == month, cancellationToken);
        if (usage is null)
        {
            db.Usage.Add(new AssetUsage { Id = Guid.CreateVersion7(), AssetBookId = value.Id, PeriodStart = month, Units = request.Units, Note = Clean(request.Note), CreatedAt = clock.UtcNow });
        }
        else
        {
            usage.Units = request.Units;
            usage.Note = Clean(request.Note);
        }

        await db.SaveChangesAsync(cancellationToken);
        var company = (await companies.FindAsync(new CompanyId(a.CompanyId), cancellationToken))!;
        await depreciation.RegenerateAsync(value, company.FunctionalCurrency.MinorUnits, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(a.Id, cancellationToken))!;
    }

    /// <summary>A change of estimate (IAS 8): the remaining amount is spread over the remaining life from the next unposted month.</summary>
    public async Task<Result<AssetSummary>> ChangeEstimatesAsync(Guid id, ChangeEstimatesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await LoadActiveAsync(id, cancellationToken);
        if (asset.IsFailure)
        {
            return asset.Error!;
        }

        var a = asset.Value;
        var value = a.Books.SingleOrDefault(v => v.BookId == request.BookId);
        if (value is null)
        {
            return Error.NotFound(BookService.EntityType, request.BookId);
        }

        if (request.Method is { } method && !DepreciationMethods.All.Contains(method, StringComparer.Ordinal))
        {
            return Error.Validation("asset.method_invalid", $"Unknown method '{method}'.");
        }

        if (request.LifeMonths is { } life && life <= 0)
        {
            return Error.Validation("asset.life_invalid", "The remaining life is at least one month.");
        }

        if (request.SalvageValue is { } salvage && (salvage < 0m || salvage > value.NetBookValue))
        {
            return Error.Validation("asset.salvage_invalid", "The salvage value is between zero and the net book value.");
        }

        var before = new { value.Method, value.LifeMonths, value.SalvageValue, value.RatePct, value.TotalUnits };
        value.Method = request.Method ?? value.Method;
        value.LifeMonths = request.LifeMonths is { } months ? WholeMonths(value.MonthsConsumed) + months : value.LifeMonths;
        value.SalvageValue = request.SalvageValue ?? value.SalvageValue;
        value.RatePct = request.RatePct ?? value.RatePct;
        value.TotalUnits = request.TotalUnits ?? value.TotalUnits;
        if (value.Status == AssetStatuses.FullyDepreciated && value.NetBookValue - value.SalvageValue > 0m)
        {
            value.Status = AssetStatuses.Active;
        }

        var company = (await companies.FindAsync(new CompanyId(a.CompanyId), cancellationToken))!;
        await depreciation.RegenerateAsync(value, company.FunctionalCurrency.MinorUnits, cancellationToken);
        a.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, a.Id, a.Number, AuditActions.Updated, Before: before, After: new { value.Method, value.LifeMonths, value.SalvageValue, value.RatePct, value.TotalUnits, book = request.BookId }, CompanyId: a.CompanyId), cancellationToken);
        return (await GetAsync(a.Id, cancellationToken))!;
    }

    // ------------------------------------------------------------------ register and reconciliation

    /// <summary>The register of a book at a date: every asset's cost, depreciation, impairment and net book value from its transactions, and the depreciation of the range.</summary>
    public async Task<Result<RegisterReport>> RegisterAsync(Guid companyId, Guid? bookId, DateOnly? from, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var book = bookId is { } b
            ? await db.Books.AsNoTracking().SingleOrDefaultAsync(x => x.Id == b && x.CompanyId == companyId, cancellationToken)
            : await db.Books.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.PostsToGl, cancellationToken);
        if (book is null)
        {
            return Error.NotFound(BookService.EntityType, bookId ?? Guid.Empty);
        }

        var at = asOf ?? clock.TodayIn(company.TimeZone);
        var start = from ?? new DateOnly(at.Year, 1, 1);
        var assets = await db.Assets.AsNoTracking().Where(a => a.CompanyId == companyId && a.Status != AssetStatuses.Draft).OrderBy(static a => a.Number).ToListAsync(cancellationToken);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(static c => c.Id, static c => c.Code, cancellationToken);
        var transactions = await db.Transactions.AsNoTracking().Where(t => t.CompanyId == companyId && t.BookId == book.Id && t.PostingDate <= at).ToListAsync(cancellationToken);
        var byAsset = transactions.ToLookup(static t => t.AssetId);
        var rows = new List<RegisterRow>();
        foreach (var asset in assets)
        {
            var ts = byAsset[asset.Id].ToList();
            if (ts.Count == 0)
            {
                continue;
            }

            var cost = ts.Sum(static t => t.CostDelta);
            var dep = ts.Sum(static t => t.AccumulatedDepreciationDelta);
            var imp = ts.Sum(static t => t.ImpairmentDelta);
            var inRange = ts.Where(t => t.PostingDate >= start && t.Kind is AssetTransactionKinds.Depreciation or AssetTransactionKinds.Reversal).Sum(static t => t.AccumulatedDepreciationDelta);
            if (cost == 0m && dep == 0m && imp == 0m && inRange == 0m && asset.Status == AssetStatuses.Disposed)
            {
                continue;
            }

            rows.Add(new RegisterRow(asset.Id, asset.Number, asset.Name.Values, categories.GetValueOrDefault(asset.CategoryId, ""), asset.Status, asset.AcquisitionDate, cost, dep, imp, cost - dep - imp, inRange));
        }

        var totals = new RegisterTotals(rows.Sum(static r => r.Cost), rows.Sum(static r => r.AccumulatedDepreciation), rows.Sum(static r => r.AccumulatedImpairment), rows.Sum(static r => r.NetBookValue), rows.Sum(static r => r.DepreciationInRange));
        return new RegisterReport(companyId, book.Id, book.Code, start, at, company.FunctionalCurrency.Code, rows, totals);
    }

    /// <summary>The subledger's totals per control role and whether the ledger agrees (the harness's fixed-asset check).</summary>
    public async Task<Result<ReconciliationReport>> ReconciliationAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var book = await db.Books.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.PostsToGl, cancellationToken);
        if (book is null)
        {
            return new ReconciliationReport(companyId, Guid.Empty, company.FunctionalCurrency.Code, [], true, []);
        }

        var transactions = await db.Transactions.AsNoTracking().Where(t => t.CompanyId == companyId && t.BookId == book.Id).ToListAsync(cancellationToken);
        var rows = new List<ReconciliationRow>
        {
            new(AccountRoles.FaCost, transactions.Sum(static t => t.CostDelta)),
            new(AccountRoles.FaAccDep, transactions.Sum(static t => t.AccumulatedDepreciationDelta)),
            new(AccountRoles.FaAccImpairment, transactions.Sum(static t => t.ImpairmentDelta)),
        };
        var report = await harness.RunAsync(companyId, cancellationToken);
        var check = report.Checks.FirstOrDefault(static c => c.Code == InvariantCodes.FixedAssetsMatchRegister);
        return new ReconciliationReport(companyId, book.Id, company.FunctionalCurrency.Code, rows, check?.Passed ?? true, check?.Problems ?? []);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Result<Asset>> LoadActiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.Include(static a => a.Books).ThenInclude(static b => b.Schedule).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (asset.Status is not (AssetStatuses.Active or AssetStatuses.FullyDepreciated))
        {
            return Error.Conflict("asset.not_active", $"A {asset.Status} asset cannot take this posting.").WithWhy(("status", asset.Status));
        }

        return asset;
    }

    /// <summary>A change dated before a posted run of the asset would have to redo that run; it is refused.</summary>
    private async Task<Result> BeforeChangeAsync(Asset asset, DateOnly date, CancellationToken cancellationToken)
    {
        var glBookId = await GlBookIdAsync(asset.CompanyId, cancellationToken);
        var gl = asset.Books.Single(v => v.BookId == glBookId);
        if (gl.DepreciatedThrough is { } through && date < through)
        {
            return Error.Conflict("asset.dated_before_depreciation", "Depreciation is posted through a later month; reverse the runs after that date first.").WithWhy(("depreciatedThrough", through), ("date", date));
        }

        return Result.Success();
    }

    private async Task<Guid> GlBookIdAsync(Guid companyId, CancellationToken cancellationToken) => (await books.EnsureGlBookAsync(companyId, cancellationToken)).Id;

    private static PostingKeys Keys(Asset asset) => new(DocumentType, AssetCategoryId: asset.CategoryId, BranchId: asset.BranchId);

    private async Task<IReadOnlyDictionary<string, Guid>?> DimensionsAsync(Asset asset, CancellationToken cancellationToken) =>
        asset.DimensionSetId is { } set ? await dimensionSets.GetAsync(set, cancellationToken) : null;

    private static string RoleOf(string kind) => kind switch
    {
        BankAccountKinds.Cash => AccountRoles.Cash,
        BankAccountKinds.PettyCash => AccountRoles.PettyCash,
        _ => AccountRoles.Bank,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Weighted months rounded up to whole months, without floating point.</summary>
    private static int WholeMonths(decimal months)
    {
        var whole = (int)months;
        return months > whole ? whole + 1 : whole;
    }

    /// <summary>Months of life an asset held before go-live had consumed by its opening month, by its convention.</summary>
    private static decimal ConsumedThrough(AssetBookValue value, DateOnly through)
    {
        var first = Depreciation.FirstMonth(value.Convention, value.DepreciationStart);
        var end = new DateOnly(through.Year, through.Month, 1);
        if (end < first)
        {
            return 0m;
        }

        var consumed = Depreciation.FirstMonthWeight(value.Convention, value.DepreciationStart);
        for (var month = first.AddMonths(1); month <= end; month = month.AddMonths(1))
        {
            consumed += 1m;
        }

        return Math.Min(consumed, value.LifeMonths);
    }

    /// <summary>The category's account codes become posting rules of the company keyed by the category, so the journals land where the admin said.</summary>
    private async Task<Result> EnsureCategoryRulesAsync(CompanyInfo company, AssetCategory category, CancellationToken cancellationToken)
    {
        var mappings = new (string Role, string? Code)[] { (AccountRoles.FaCost, category.CostAccountCode), (AccountRoles.FaAccDep, category.AccumulatedDepreciationAccountCode), (AccountRoles.DepreciationExpense, category.DepreciationExpenseAccountCode) };
        if (mappings.All(static m => m.Code is null))
        {
            return Result.Success();
        }

        var chartId = await chart.ChartForCompanyAsync(company.Id, cancellationToken);
        if (chartId is null)
        {
            return Error.Validation("asset.chart_required", "The company has no chart of accounts yet.");
        }

        foreach (var (role, code) in mappings.Where(static m => m.Code is not null))
        {
            var account = await chart.FindAccountByCodeAsync(chartId.Value, code!, cancellationToken);
            if (account is null || account.IsHeader || !account.IsActive)
            {
                return Error.Validation("asset.account_unknown", $"Category {category.Code} names account '{code}' for {role}, which the company's chart does not have as a postable account.").WithWhy(("category", category.Code), ("accountCode", code), ("role", role));
            }

            var ensured = await postingRules.EnsureRuleAsync(company.Id, role, new PostingKeys(AssetCategoryId: category.Id), account.Id, cancellationToken);
            if (ensured.IsFailure)
            {
                return ensured.Error!;
            }
        }

        return Result.Success();
    }

    /// <summary>The credit side of an acquisition or addition by its source, with the tax a supplier charges; returns the lines and the cost to capitalise (net, plus tax that cannot be recovered).</summary>
    private async Task<Result<(List<PostingLine> Lines, decimal CostBasis)>> CreditSideAsync(Asset asset, CompanyInfo company, decimal amount, DateOnly date, string source, Guid? supplierId, Guid? bankAccountId, Guid? taxCodeId, DateOnly? dueDate, decimal openingDepreciation, Guid transactionId, string? reference, CancellationToken cancellationToken)
    {
        var lines = new List<PostingLine>();
        var keys = new PostingKeys(DocumentType, AssetCategoryId: asset.CategoryId, BranchId: asset.BranchId);
        var costBasis = amount;
        switch (source)
        {
            case AcquisitionSources.Supplier:
                {
                    if (supplierId is not { } partnerId)
                    {
                        return Error.Validation("asset.supplier_required", "An acquisition from a supplier names the supplier.");
                    }

                    var supplier = await partners.FindSupplierAsync(asset.CompanyId, partnerId, cancellationToken);
                    var gross = amount;
                    if (taxCodeId is { } codeId)
                    {
                        var taxed = await tax.CalculateAsync(new TaxDocumentRequest(asset.CompanyId, TaxDirections.Purchase, date, company.FunctionalCurrency.Code, false, [new TaxDocumentLine("1", amount, TaxCodeId: codeId)], partnerId), cancellationToken);
                        if (taxed.IsFailure)
                        {
                            return taxed.Error!;
                        }

                        var line = taxed.Value.Document.Lines[0];
                        if (line.Tax != 0m)
                        {
                            var code = await tax.FindCodeAsync(codeId, date, cancellationToken);
                            if (code.IsFailure)
                            {
                                return code.Error!;
                            }

                            if (line.IsRecoverable)
                            {
                                lines.Add(new PostingLine(code.Value.InputAccountRole, line.Tax, keys, PartnerId: partnerId, TaxCodeId: codeId, TaxBase: amount));
                            }
                            else
                            {
                                costBasis += line.Tax;
                            }

                            if (line.IsReverseCharge)
                            {
                                lines.Add(new PostingLine(code.Value.OutputAccountRole, -line.Tax, keys, PartnerId: partnerId, TaxCodeId: codeId, TaxBase: amount));
                            }
                            else
                            {
                                gross += line.Tax;
                            }

                            var recorded = await taxLedger.RecordAsync(new TaxPostingRequest(asset.CompanyId, date, date, "fixed_assets", DocumentType, transactionId, asset.Number, null, partnerId, company.FunctionalCurrency.Code,
                                [new TaxEntryRequest(codeId, TaxDirections.Purchase, line.RatePct, amount, line.Tax, amount, line.Tax, line.IsReverseCharge, line.IsRecoverable, transactionId)]), cancellationToken);
                            if (recorded.IsFailure)
                            {
                                return recorded.Error!;
                            }
                        }
                    }

                    lines.Add(new PostingLine(AccountRoles.AP, -gross, new PostingKeys(DocumentType, PartnerPostingGroupId: supplier?.PostingGroupId), PartnerId: partnerId, SubledgerType: SubledgerTypes.Payables, SubledgerRef: transactionId, DueDate: dueDate ?? date));
                    break;
                }

            case AcquisitionSources.Bank:
                {
                    var bank = bankAccountId is { } bankId ? await bankAccounts.FindAsync(bankId, cancellationToken) : null;
                    if (bank is null || bank.CompanyId != asset.CompanyId)
                    {
                        return Error.NotFound("bank_account", bankAccountId ?? Guid.Empty);
                    }

                    lines.Add(new PostingLine(RoleOf(bank.Kind), -amount, new PostingKeys(DocumentType, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: Guid.CreateVersion7()));
                    break;
                }

            case AcquisitionSources.Clearing:
                lines.Add(new PostingLine(AccountRoles.FaClearing, -amount, keys, PartnerId: asset.SupplierPartnerId));
                break;
            case AcquisitionSources.Opening:
                if (openingDepreciation > 0m)
                {
                    lines.Add(new PostingLine(AccountRoles.FaAccDep, -openingDepreciation, keys, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transactionId));
                }

                lines.Add(new PostingLine(AccountRoles.OpeningBalanceEquity, -(amount - openingDepreciation), keys));
                break;
            default:
                return Error.Validation("asset.source_invalid", $"Unknown source '{source}'.");
        }

        return (lines, costBasis);
    }

    /// <summary>After the journal: the open item a supplier source needs, the bank movement a bank source needs.</summary>
    private async Task<Result> AfterCreditAsync(Asset asset, CompanyInfo company, decimal costBasis, DateOnly date, string source, Guid? supplierId, Guid? bankAccountId, DateOnly? dueDate, Guid transactionId, string documentNumber, string? reference, PostingResult posted, CancellationToken cancellationToken)
    {
        switch (source)
        {
            case AcquisitionSources.Supplier:
                {
                    var apLine = posted.Lines.Single(l => l.SubledgerType == SubledgerTypes.Payables && l.SubledgerRef == transactionId);
                    await payables.OpenAsync(new NewOpenItem(asset.CompanyId, supplierId!.Value, PayableKinds.Invoice, DocumentType, transactionId, documentNumber, 1, reference, date, date, dueDate ?? date, null, 0m,
                        company.FunctionalCurrency.Code, apLine.CreditTc - apLine.DebitTc, apLine.CreditFc - apLine.DebitFc, posted.RateTcFc, posted.EntryId, asset.BranchId, asset.DimensionSetId), cancellationToken);
                    break;
                }

            case AcquisitionSources.Bank:
                {
                    var bankLine = posted.Lines.Single(l => l.SubledgerType == SubledgerTypes.Bank);
                    await bankTransactions.RecordAsync(new NewBankTransaction(bankLine.SubledgerRef!.Value, bankAccountId!.Value, asset.CompanyId, date, "disbursement", -costBasis, -costBasis, reference ?? documentNumber, DocumentType, transactionId, posted.EntryId), cancellationToken);
                    break;
                }
        }

        return Result.Success();
    }

    private AssetTransaction Transaction(Asset asset, AssetBookValue value, Guid id, DateOnly date, string kind, Guid? entryId, string? reference, decimal costDelta = 0m, decimal accumulatedDepreciationDelta = 0m, decimal impairmentDelta = 0m, decimal surplusDelta = 0m, decimal proceeds = 0m, decimal gainLoss = 0m, string detail = "{}") => new()
    {
        Id = id,
        CompanyId = asset.CompanyId,
        AssetId = asset.Id,
        BookId = value.BookId,
        PostingDate = date,
        Kind = kind,
        CostDelta = costDelta,
        AccumulatedDepreciationDelta = accumulatedDepreciationDelta,
        ImpairmentDelta = impairmentDelta,
        SurplusDelta = surplusDelta,
        Proceeds = proceeds,
        GainLoss = gainLoss,
        JournalEntryId = entryId,
        Reference = reference,
        Detail = detail,
        CreatedAt = clock.UtcNow,
    };

    private static AssetSummary Map(Asset a, AssetCategory? category, Guid? glBookId, IReadOnlyDictionary<Guid, AssetBook>? bookRows, IReadOnlyList<AssetTransaction>? transactions, IReadOnlyList<AssetVerification>? verifications, string? supplierCode, IReadOnlyDictionary<string, Guid>? dimensions)
    {
        var gl = a.Books.FirstOrDefault(b => b.BookId == glBookId) ?? a.Books.FirstOrDefault();
        IReadOnlyList<AssetBookValueSummary>? books = bookRows is null ? null : a.Books.OrderByDescending(b => bookRows.GetValueOrDefault(b.BookId)?.PostsToGl ?? false).ThenBy(b => bookRows.GetValueOrDefault(b.BookId)?.Code, StringComparer.Ordinal).Select(b =>
            new AssetBookValueSummary(b.Id, b.BookId, bookRows.GetValueOrDefault(b.BookId)?.Code ?? "", bookRows.GetValueOrDefault(b.BookId)?.PostsToGl ?? false, b.Method, b.LifeMonths, b.SalvageValue, b.RatePct, b.Convention, b.DepreciationStart, b.TotalUnits, b.Cost, b.AccumulatedDepreciation, b.AccumulatedImpairment, b.RevaluationSurplus, b.NetBookValue, b.MonthsConsumed, b.DepreciatedThrough, b.Status,
                b.Schedule.OrderBy(static s => s.PeriodStart).Select(static s => new ScheduleLineSummary(s.Id, s.PeriodStart, s.PeriodEnd, s.Weight, s.PlannedAmount, s.PostedAmount, s.Status, s.RunId)).ToList())).ToList();
        return new AssetSummary(a.Id, a.CompanyId, a.Number, a.Name.Values, a.Description, a.CategoryId, category?.Code ?? "", category?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), a.ParentAssetId, a.AcquisitionDate, a.InServiceDate, a.Cost, a.Currency, a.Quantity, a.Unit,
            a.BranchId, a.DimensionSetId, dimensions, a.CustodianMembershipId, a.SerialNumber, a.SupplierPartnerId, supplierCode, a.SupplierReference, a.WarrantyUntil, a.Status, a.AcquisitionSource, a.AcquisitionBankAccountId, a.AcquisitionTaxCodeId, a.AcquisitionDueDate, a.OpeningAccumulatedDepreciation, a.AcquisitionEntryId, a.DisposedOn, a.DisposalKind, a.Notes, a.CustomFields,
            gl?.Method, gl?.LifeMonths, gl?.AccumulatedDepreciation ?? 0m, gl?.AccumulatedImpairment ?? 0m, gl?.NetBookValue ?? a.Cost, gl?.DepreciatedThrough, a.CreatedAt, a.UpdatedAt, books,
            transactions?.Select(t => new AssetTransactionSummary(t.Id, t.BookId, bookRows?.GetValueOrDefault(t.BookId)?.Code ?? "", t.PostingDate, t.Kind, t.CostDelta, t.AccumulatedDepreciationDelta, t.ImpairmentDelta, t.SurplusDelta, t.Proceeds, t.GainLoss, t.JournalEntryId, t.ReversesId, t.RunId, t.Reference, t.CreatedAt)).ToList(),
            verifications?.Select(static v => new AssetVerificationSummary(v.Id, v.VerifiedOn, v.VerifiedBy, v.Condition, v.LocationNote, v.Note)).ToList());
    }
}

internal static class AssetExtensions
{
    /// <summary>The month an opening asset's depreciation runs through: what was given, else the acquisition date.</summary>
    public static DateOnly OpeningDepreciatedThroughOrAcquisition(this Asset asset) => asset.OpeningDepreciatedThrough ?? asset.AcquisitionDate;
}
