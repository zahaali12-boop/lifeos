using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Pos.Domain;
using Quicker.Pos.Persistence;
using Quicker.Pricing.Contracts;
using Quicker.Tax.Contracts;

namespace Quicker.Pos.Application;

/// <summary>
/// Registers (A-180): a till's configuration, and the catalogue it caches to sell offline: every sellable item in
/// its sales unit, priced through the pricing engine for the register's price list and walk-in customer on the
/// register's channel, and taxed as the tax engine would tax a sale to that customer today.
/// </summary>
public sealed class RegisterService(
    PosDbContext db,
    ICompanyDirectory companies,
    IWarehouseDirectory warehouses,
    IBankAccountDirectory bankAccounts,
    IPartnerDirectory partners,
    IItemDirectory items,
    IPricing pricing,
    ITaxDetermination tax,
    IAuditSink audit,
    IClock clock)
{
    public const string AuditEntity = "pos_register";

    /// <summary>The pricing channel of the point of sale (A-009 reserved it).</summary>
    public const string Channel = "pos";

    public async Task<IReadOnlyList<PosRegisterSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Registers.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        var registers = await query.OrderBy(static r => r.Code).ToListAsync(cancellationToken);
        var result = new List<PosRegisterSummary>(registers.Count);
        foreach (var register in registers)
        {
            result.Add(await MapAsync(register, cancellationToken));
        }

        return result;
    }

    public async Task<Result<PosRegisterSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var register = await db.Registers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return register is null ? Error.NotFound(AuditEntity, id) : await MapAsync(register, cancellationToken);
    }

    public async Task<Result<PosRegisterSummary>> SaveAsync(Guid? id, SaveRegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Validation.Name(request.Name, "register");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var code = Validation.UpperCode(string.IsNullOrWhiteSpace(request.Code) ? Validation.CodeFromName(name.Value) : request.Code, "register");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != request.CompanyId)
        {
            return Error.NotFound("warehouse", request.WarehouseId);
        }

        if (request.BinId is { } binId)
        {
            var bin = await warehouses.FindBinAsync(binId, cancellationToken);
            if (bin is null || bin.WarehouseId != warehouse.Id)
            {
                return Error.Validation("register.bin_not_in_warehouse", "The bin is not one of the warehouse's bins.").WithWhy(("binId", binId));
            }
        }
        else if (warehouse.BinsEnabled)
        {
            return Error.Validation("register.bin_required", "The warehouse uses bins; name the bin the till sells from.").WithWhy(("warehouse", warehouse.Code));
        }

        var cash = await bankAccounts.FindAsync(request.CashAccountId, cancellationToken);
        if (cash is null || cash.CompanyId != request.CompanyId)
        {
            return Error.NotFound("bank_account", request.CashAccountId);
        }

        if (cash.Kind is not (BankAccountKinds.Cash or BankAccountKinds.PettyCash))
        {
            return Error.Validation("register.cash_account_kind", "The till's cash goes into a cash box (a cash or petty cash account), not a bank account.").WithWhy(("kind", cash.Kind));
        }

        if (!string.Equals(cash.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            return Error.Validation("register.currency", "A till sells in the company's currency, so its cash box is in that currency.").WithWhy(("cashBox", cash.Currency), ("company", company.FunctionalCurrency.Code));
        }

        if (request.CardAccountId is { } cardId)
        {
            var card = await bankAccounts.FindAsync(cardId, cancellationToken);
            if (card is null || card.CompanyId != request.CompanyId)
            {
                return Error.NotFound("bank_account", cardId);
            }

            if (!string.Equals(card.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
            {
                return Error.Validation("register.currency", "The card account is in the company's currency.").WithWhy(("cardAccount", card.Currency), ("company", company.FunctionalCurrency.Code));
            }
        }

        if (request.DefaultPartnerId is { } partnerId)
        {
            var partner = await partners.FindAsync(partnerId, cancellationToken);
            if (partner is null || !partner.IsCustomer)
            {
                return Error.Validation("register.default_partner_not_customer", "The walk-in customer is a partner with the customer role.").WithWhy(("partnerId", partnerId));
            }
        }

        if (request.CashRoundingIncrement is < 0m)
        {
            return Error.Validation("register.rounding_invalid", "The cash rounding increment is zero (the company's setting) or above.");
        }

        PosRegister? register = null;
        if (id is { } existing)
        {
            register = await db.Registers.SingleOrDefaultAsync(r => r.Id == existing, cancellationToken);
            if (register is null)
            {
                return Error.NotFound(AuditEntity, existing);
            }

            if (register.CompanyId != request.CompanyId)
            {
                return Error.Validation("register.company_fixed", "A register stays in its company.");
            }
        }

        if (await db.Registers.AnyAsync(r => r.CompanyId == request.CompanyId && r.Code == code.Value && (register == null || r.Id != register.Id), cancellationToken))
        {
            return Error.Conflict("register.code_taken", $"The company already has a register '{code.Value}'.");
        }

        var before = register is null ? null : Snapshot(register);
        var now = clock.UtcNow;
        register ??= new PosRegister { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = now };
        register.Code = code.Value;
        register.Name = name.Value;
        register.BranchId = request.BranchId;
        register.WarehouseId = request.WarehouseId;
        register.BinId = request.BinId;
        register.CashAccountId = request.CashAccountId;
        register.CardAccountId = request.CardAccountId;
        register.PriceListId = request.PriceListId;
        register.DefaultPartnerId = request.DefaultPartnerId;
        register.AllowCredit = request.AllowCredit;
        register.AllowPriceOverride = request.AllowPriceOverride;
        register.CashRoundingIncrement = request.CashRoundingIncrement ?? 0m;
        register.ReceiptFooter = Validation.Text(request.ReceiptFooter);
        register.IsActive = request.IsActive;
        register.UpdatedAt = now;
        if (id is null)
        {
            db.Registers.Add(register);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, register.Id, register.Code, id is null ? AuditActions.Created : AuditActions.Updated, Before: before, After: Snapshot(register), CompanyId: register.CompanyId), cancellationToken);
        return await MapAsync(register, cancellationToken);
    }

    /// <summary>What the till caches: the register's rules and every sellable item priced and taxed as of today in the company.</summary>
    public async Task<Result<PosCatalogue>> CatalogueAsync(Guid registerId, CancellationToken cancellationToken)
    {
        var register = await db.Registers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == registerId, cancellationToken);
        if (register is null)
        {
            return Error.NotFound(AuditEntity, registerId);
        }

        var company = (await companies.FindAsync(new CompanyId(register.CompanyId), cancellationToken))!;
        var currency = company.FunctionalCurrency;
        var today = clock.TodayIn(company.TimeZone);
        var sellable = await items.SellableAsync(cancellationToken);
        var entries = new List<(string Key, SellableItem Item, ItemVariantInfo? Variant)>();
        foreach (var item in sellable)
        {
            if (item.HasVariants && item.Variants.Count > 0)
            {
                foreach (var variant in item.Variants)
                {
                    entries.Add(($"{item.Id:N}:{variant.Id:N}", item, variant));
                }
            }
            else
            {
                entries.Add((item.Id.ToString("N"), item, null));
            }
        }

        // One basket of every entry at quantity one: the price each sells at today on the register's price list, for its walk-in customer.
        var priced = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        bool? pricesIncludeTax = null;
        if (entries.Count > 0)
        {
            var lines = entries.Select(e => new PricingLineRequest(e.Key, e.Item.Id, e.Variant?.Id, e.Item.SalesUomId, 1m)).ToList();
            var result = await pricing.PriceAsync(new PricingRequest(register.CompanyId, register.DefaultPartnerId, currency.Code, today, lines, register.PriceListId, Channel), cancellationToken);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            pricesIncludeTax = result.Value.PricesIncludeTax;
            foreach (var line in result.Value.Lines)
            {
                priced[line.Key] = line.Problem is null ? line.UnitPrice : null;
            }
        }

        // The tax each entry takes on a sale to the walk-in customer today; the code is pinned on the till so a sale carries it back.
        var taxed = new Dictionary<string, TaxedLine>(StringComparer.Ordinal);
        var roundingLevel = TaxRoundingLevels.Line;
        if (entries.Count > 0)
        {
            var taxLines = entries.Select(e => new TaxDocumentLine(e.Key, priced.GetValueOrDefault(e.Key) ?? 1m, e.Item.Id)).ToList();
            var result = await tax.CalculateAsync(new TaxDocumentRequest(register.CompanyId, TaxDirections.Sales, today, currency.Code, pricesIncludeTax ?? false, taxLines, register.DefaultPartnerId), cancellationToken);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            roundingLevel = result.Value.Document.RoundingLevel;
            foreach (var line in result.Value.Document.Lines)
            {
                taxed[line.Key] = line;
            }
        }

        var catalogue = new List<PosCatalogueItem>(entries.Count);
        foreach (var (key, item, variant) in entries)
        {
            var t = taxed.GetValueOrDefault(key);
            var barcodes = item.Barcodes.Where(b => b.UomId == item.SalesUomId && (variant is null ? b.VariantId is null : b.VariantId == variant.Id || b.VariantId is null)).Select(static b => b.Barcode).ToList();
            catalogue.Add(new PosCatalogueItem(item.Id, variant?.Id, variant?.Sku ?? item.Code, (variant is null || variant.Name.IsEmpty ? item.Name : variant.Name).Values, item.SalesUomId, item.SalesUomCode,
                priced.GetValueOrDefault(key), t?.TaxCodeId, t?.TaxCode, t?.RatePct ?? 0m, item.IsStockItem, item.Tracking, item.CategoryId, barcodes));
        }

        var rounding = await RoundingIncrementAsync(register, company, cancellationToken);
        var roundingMode = company.RoundingMode == RoundingMode.HalfEven ? "half_even" : "half_away_from_zero";
        return new PosCatalogue(register.Id, register.CompanyId, currency.Code, currency.MinorUnits, pricesIncludeTax ?? false, roundingLevel, roundingMode, rounding, register.AllowCredit, register.AllowPriceOverride, register.DefaultPartnerId, clock.UtcNow, catalogue);
    }

    /// <summary>The increment cash is rounded to on this register: its own, else the company's setting for the currency, else none.</summary>
    public async Task<decimal> RoundingIncrementAsync(PosRegister register, CompanyInfo company, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(company);
        if (register.CashRoundingIncrement > 0m)
        {
            return register.CashRoundingIncrement;
        }

        var setting = await companies.FindCompanyCurrencyAsync(company.Id, company.FunctionalCurrency.Code, cancellationToken);
        return setting?.CashRoundingIncrement ?? 0m;
    }

    internal async Task<PosRegisterSummary> MapAsync(PosRegister r, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(r.CompanyId), cancellationToken);
        var warehouse = await warehouses.FindAsync(r.WarehouseId, cancellationToken);
        var cash = await bankAccounts.FindAsync(r.CashAccountId, cancellationToken);
        var card = r.CardAccountId is { } cardId ? await bankAccounts.FindAsync(cardId, cancellationToken) : null;
        var partner = r.DefaultPartnerId is { } partnerId ? await partners.FindAsync(partnerId, cancellationToken) : null;
        var openSession = await db.Sessions.AsNoTracking().Where(s => s.RegisterId == r.Id && s.Status == SessionStatuses.Open).Select(static s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        var rounding = company is null ? r.CashRoundingIncrement : await RoundingIncrementAsync(r, company, cancellationToken);
        return new PosRegisterSummary(r.Id, r.CompanyId, r.BranchId, r.Code, r.Name.Values, r.WarehouseId, warehouse?.Code, r.BinId, r.CashAccountId, cash?.Code, r.CardAccountId, card?.Code, r.PriceListId,
            r.DefaultPartnerId, partner?.LegalName.Values, r.AllowCredit, r.AllowPriceOverride, rounding, company?.FunctionalCurrency.Code ?? cash?.Currency ?? string.Empty, r.ReceiptFooter.Values, r.IsActive, openSession, r.CreatedAt, r.UpdatedAt);
    }

    private static object Snapshot(PosRegister r) => new
    {
        r.Code, name = r.Name, r.BranchId, r.WarehouseId, r.BinId, r.CashAccountId, r.CardAccountId, r.PriceListId, r.DefaultPartnerId, r.AllowCredit, r.AllowPriceOverride, r.CashRoundingIncrement, r.IsActive,
    };

    internal static string Text(LocalizedText text, string language) => text.Resolve(language, "en");

    /// <summary>Rounds to the company's currency under the company's rounding mode, as the tax engine and the ledger do.</summary>
    internal static decimal Round(decimal amount, CompanyInfo company) => new RoundingPolicy(company.RoundingMode).Round(amount, company.FunctionalCurrency.MinorUnits);
}
