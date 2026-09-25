using Quicker.Kernel.Results;

namespace Quicker.Pricing.Contracts;

/// <summary>Where a line's base price came from, in the order the engine looks (ADR-0030 step 1); the first that prices the item wins.</summary>
public static class PriceSources
{
    public const string Manual = "manual";
    public const string Agreement = "agreement";
    public const string DocumentList = "document_list";
    public const string CustomerList = "customer_list";
    public const string GroupList = "group_list";
    public const string DefaultList = "default_list";
    public const string ItemListPrice = "item_list_price";

    public static readonly IReadOnlyList<string> All = [Manual, Agreement, DocumentList, CustomerList, GroupList, DefaultList, ItemListPrice];
}

public static class DiscountLevels
{
    public const string Line = "line";
    public const string Document = "document";

    public static readonly IReadOnlyList<string> All = [Line, Document];
}

public static class DiscountValueTypes
{
    public const string Percentage = "percentage";
    public const string Amount = "amount";
    public const string FixedPrice = "fixed_price";

    public static readonly IReadOnlyList<string> All = [Percentage, Amount, FixedPrice];
}

/// <summary>Exclusive rules compete and the best for the customer wins; stackable rules apply one after the other on the running net.</summary>
public static class Combinations
{
    public const string Exclusive = "exclusive";
    public const string Stackable = "stackable";

    public static readonly IReadOnlyList<string> All = [Exclusive, Stackable];
}

public static class PromotionKinds
{
    public const string BuyXGetY = "buy_x_get_y";
    public const string Bundle = "bundle";
    public const string VolumeTier = "volume_tier";
    public const string Coupon = "coupon";

    public static readonly IReadOnlyList<string> All = [BuyXGetY, Bundle, VolumeTier, Coupon];
}

public static class PriceRoundingModes
{
    public const string Nearest = "nearest";
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlyList<string> All = [Nearest, Up, Down];
}

public static class FloorBreachActions
{
    public const string Block = "block";
    public const string Warn = "warn";

    public static readonly IReadOnlyList<string> All = [Block, Warn];
}

/// <summary>The steps of the pipeline as they appear in a line's explanation.</summary>
public static class PriceStepKinds
{
    public const string BasePrice = "base_price";
    public const string Derivation = "derivation";
    public const string Unit = "unit";
    public const string Currency = "currency";
    public const string LineDiscount = "line_discount";
    public const string Promotion = "promotion";
    public const string DocumentDiscount = "document_discount";
    public const string Floor = "floor";
}

/// <summary>What happened to a rule or source the engine considered for a step.</summary>
public static class CandidateOutcomes
{
    public const string Won = "won";
    public const string Applied = "applied";
    public const string Lost = "lost";
    public const string NoPrice = "no_price";
    public const string NotValid = "not_valid";
    public const string Inactive = "inactive";
    public const string ConditionNotMet = "condition_not_met";
    public const string NoSaving = "no_saving";
    public const string UsageLimitReached = "usage_limit_reached";
    public const string CouponMissing = "coupon_missing";
    public const string LineTaken = "line_taken";
    public const string Skipped = "skipped";
    public const string Passed = "passed";
    public const string Breached = "breached";
}

/// <summary>One line to price: an item (or one of its variants) in one of its units; no unit means the item's sales unit, else its base unit.</summary>
public sealed record PricingLineRequest(
    string Key,
    Guid ItemId,
    Guid? VariantId,
    Guid? UomId,
    decimal Quantity,
    decimal? ManualUnitPrice = null,
    decimal? ManualDiscountPct = null);

/// <summary>
/// A basket to price for a customer (or no one in particular) in a company on a date. The currency defaults to the
/// customer's account currency, else the company's; the pricing date to today in the company; the tax basis to the
/// basis of the prices found.
/// </summary>
public sealed record PricingRequest(
    Guid CompanyId,
    Guid? PartnerId,
    string? Currency,
    DateOnly? PricingDate,
    IReadOnlyList<PricingLineRequest> Lines,
    Guid? PriceListId = null,
    string? Channel = null,
    Guid? PaymentTermsId = null,
    bool? PricesIncludeTax = null,
    IReadOnlyList<string>? CouponCodes = null,
    decimal? DocumentDiscountPct = null,
    string? RateType = null);

/// <summary>A fact behind a step (a rate, a quantity break, a rounding), as an invariant string for display.</summary>
public sealed record PriceFact(string Key, string Value);

/// <summary>A rule or source the engine looked at for a step and what became of it.</summary>
public sealed record PriceCandidate(string Source, string? RefType, Guid? RefId, string? RefCode, string Outcome, decimal? Amount = null, string? Detail = null);

/// <summary>
/// One step of a line's price (ADR-0030): what it started from and ended at (unit price for the price steps, the line's
/// running net for the discount steps), the amount it took off, the rule or source that decided it, the facts behind it
/// and the other candidates considered.
/// </summary>
public sealed record PriceStep(
    string Kind,
    string? Source,
    string? RefType,
    Guid? RefId,
    string? RefCode,
    decimal? Before,
    decimal? After,
    decimal? Amount,
    IReadOnlyList<PriceFact> Facts,
    IReadOnlyList<PriceCandidate> Candidates);

/// <summary>Why a line could not be priced; the steps before the problem stay in the explanation.</summary>
public sealed record PriceProblem(string Code, string Message);

/// <summary>The line's price against the floor that governs it (the item's, else its nearest category's).</summary>
public sealed record PriceFloorCheck(
    Guid FloorId,
    string Scope,
    Guid ScopeId,
    decimal? MinPrice,
    decimal? MinMarginPct,
    decimal NetPerBaseUnit,
    decimal? UnitCost,
    decimal? MarginPct,
    bool Breached,
    string OnBreach,
    string? Reason);

public sealed record PricedLine(
    string Key,
    Guid ItemId,
    string ItemCode,
    Guid? VariantId,
    Guid UomId,
    string UomCode,
    decimal Quantity,
    decimal BaseQuantity,
    string? PriceSource,
    bool? PricesIncludeTax,
    decimal UnitPrice,
    decimal GrossAmount,
    decimal LineDiscountAmount,
    decimal PromotionDiscountAmount,
    decimal DocumentDiscountAmount,
    decimal NetAmount,
    decimal NetUnitPrice,
    decimal EffectiveDiscountPct,
    bool IsFreeGoods,
    Guid? PromotionId,
    string? PromotionCode,
    string? FreeGoodsForKey,
    PriceFloorCheck? Floor,
    PriceProblem? Problem,
    IReadOnlyList<PriceStep> Steps);

public sealed record AppliedPromotion(Guid PromotionId, string Code, string Kind, string Combination, decimal Benefit, IReadOnlyList<string> LineKeys);

/// <summary>A priced basket: every line with its explanation, the promotions and document discounts applied, and the totals.</summary>
public sealed record PricingResult(
    Guid CompanyId,
    Guid? PartnerId,
    string Currency,
    DateOnly PricingDate,
    string RateType,
    bool? PricesIncludeTax,
    IReadOnlyList<PricedLine> Lines,
    IReadOnlyList<AppliedPromotion> Promotions,
    IReadOnlyList<PriceStep> DocumentSteps,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal NetAmount,
    int UnpricedLines,
    bool HasFloorBlocks,
    bool HasFloorWarnings);

/// <summary>The promotions a confirmed document used, counted against their limits until the document gives them back.</summary>
public sealed record PromotionUsageRequest(Guid CompanyId, Guid? PartnerId, string DocumentType, Guid DocumentId, IReadOnlyList<Guid> PromotionIds);

/// <summary>The pricing engine (ADR-0030): deterministic, explained, the same answer for the same inputs and rules as of the pricing date.</summary>
public interface IPricing
{
    Task<Result<PricingResult>> PriceAsync(PricingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the use of promotions by a document under a lock on each promotion, refusing
    /// (<c>promotion.usage_limit_reached</c>) when a limit is already used up, so two documents confirmed at once cannot
    /// both take the last use. Recording the same document again is a no-op.
    /// </summary>
    Task<Result> RecordPromotionUsageAsync(PromotionUsageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gives back the promotions a document used (cancelled or reversed).</summary>
    Task<Result> ReleasePromotionUsageAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default);
}
