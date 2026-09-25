using System.Globalization;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Pricing.Contracts;

namespace Quicker.Pricing.Engine;

/// <summary>
/// The pricing pipeline of ADR-0030 as a pure function of its input: base price (manual, agreement, lists in order,
/// the item's list price), currency, line discounts, promotions over the basket, document discounts, rounding and
/// floors. Every choice sorts by explicit keys (never by the order rules were read or created), so the same input
/// always gives the same result and explanation (A-144).
/// </summary>
public static class PriceEngine
{
    public static PricingResult Price(PricingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new PricingRun(input).Execute();
    }

    /// <summary>The decimals a unit price keeps in a currency: two more than its minor unit, at most ten (A-144).</summary>
    public static int PriceDecimals(Currency currency) => Math.Min(currency.MinorUnits + 2, 10);

    internal static RoundingDirection Direction(string mode) => mode switch
    {
        PriceRoundingModes.Up => RoundingDirection.Up,
        PriceRoundingModes.Down => RoundingDirection.Down,
        _ => RoundingDirection.Nearest,
    };

    internal static string Format(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    internal static bool ValidOn(DateOnly? from, DateOnly? to, DateOnly date) => (from is null || from <= date) && (to is null || to >= date);
}

internal sealed class PricingRun(PricingInput input)
{
    private const int MaxDerivationDepth = 5;

    private readonly PricingContext _ctx = input.Context;
    private readonly PricingRuleSet _rules = input.Rules;
    private readonly IReadOnlyDictionary<Guid, EngineTaxRate> _taxRates = input.TaxRates ?? new Dictionary<Guid, EngineTaxRate>();
    private readonly List<LineState> _lines = [];
    private readonly List<PriceStep> _documentSteps = [];
    private readonly List<AppliedPromotion> _applied = [];
    private readonly Dictionary<Guid, List<EngineListItem>> _listItems = input.Rules.ListItems.GroupBy(static e => e.ListId).ToDictionary(static g => g.Key, static g => g.ToList());
    private readonly Dictionary<Guid, EngineList> _lists = input.Rules.Lists.ToDictionary(static l => l.Id);
    private bool? _documentBasis = input.Context.PricesIncludeTax;

    private int Minor => _ctx.Currency.MinorUnits;

    private int PriceDecimals => PriceEngine.PriceDecimals(_ctx.Currency);

    public PricingResult Execute()
    {
        foreach (var line in input.Lines)
        {
            _lines.Add(PriceLine(line));
        }

        ApplyPromotions();
        ApplyDocumentDiscounts();
        foreach (var line in _lines.Where(static l => l.Problem is null && !l.IsFree))
        {
            CheckFloor(line);
        }

        var priced = _lines.Where(static l => l.Problem is null).ToList();
        return new PricingResult(
            _ctx.CompanyId,
            _ctx.PartnerId,
            _ctx.Currency.Code,
            _ctx.PricingDate,
            _ctx.RateType,
            _documentBasis,
            _lines.Select(ToPricedLine).ToList(),
            _applied,
            _documentSteps,
            priced.Sum(static l => l.Gross),
            priced.Sum(static l => l.LineDiscount + l.PromotionDiscount + l.DocumentDiscount),
            priced.Sum(static l => l.Running),
            _lines.Count(static l => l.Problem is not null),
            priced.Any(static l => l.Floor is { Breached: true, OnBreach: FloorBreachActions.Block }),
            priced.Any(static l => l.Floor is { Breached: true, OnBreach: FloorBreachActions.Warn }));
    }

    // ------------------------------------------------------------------ one line: base price, currency, line discounts

    private LineState PriceLine(EngineLine request)
    {
        var line = new LineState { Key = request.Key, Quantity = request.Quantity, VariantId = request.VariantId, ManualDiscountPct = request.ManualDiscountPct };
        if (!input.Items.TryGetValue(request.ItemId, out var item))
        {
            line.ItemId = request.ItemId;
            return line.Fail("pricing.item_unknown", "The item does not exist.");
        }

        line.Item = item;
        line.ItemId = item.Id;
        var uomId = request.UomId ?? item.SalesUomId ?? item.BaseUomId;
        var uom = item.Uoms.FirstOrDefault(u => u.UomId == uomId);
        if (uom is null)
        {
            return line.Fail("pricing.uom_not_item_unit", "The unit is not one of the item's units.");
        }

        line.Uom = uom;
        if (!item.IsActive)
        {
            return line.Fail("pricing.item_inactive", "The item is inactive.");
        }

        if (request.Quantity <= 0)
        {
            return line.Fail("pricing.quantity_not_positive", "The quantity must be greater than zero.");
        }

        var baseQuantity = ItemUomMath.ToBase(request.Quantity, uom.Numerator, uom.Denominator, item.BasePrecision);
        if (baseQuantity.IsFailure)
        {
            return line.Fail(baseQuantity.Error!.Code, baseQuantity.Error.Message);
        }

        line.BaseQuantity = baseQuantity.Value;
        if (!ResolveUnitPrice(line, request.ManualUnitPrice))
        {
            return line;
        }

        ApplyLineDiscounts(line);
        return line;
    }

    /// <summary>Steps 1 and 2: the base price per line unit and its conversion to the document currency.</summary>
    private bool ResolveUnitPrice(LineState line, decimal? manualUnitPrice)
    {
        Quote? quote;
        if (manualUnitPrice is { } manual)
        {
            quote = new Quote(manual, _ctx.Currency.Code, null, PriceSources.Manual, null, null, null, [], []);
            line.Steps.Add(Step(PriceStepKinds.BasePrice, PriceSources.Manual, null, null, null, null, manual, null, [Fact("currency", _ctx.Currency.Code)], [new PriceCandidate(PriceSources.Manual, null, null, null, CandidateOutcomes.Won, manual)]));
        }
        else
        {
            var candidates = new List<PriceCandidate>();
            try
            {
                quote = FindBasePrice(line, candidates);
            }
            catch (MissingRateException missing)
            {
                line.Steps.Add(Step(PriceStepKinds.BasePrice, null, null, null, null, null, null, null, [], candidates));
                return RateMissing(line, missing);
            }

            if (quote is null)
            {
                line.Steps.Add(Step(PriceStepKinds.BasePrice, null, null, null, null, null, null, null, [], candidates));
                line.Fail("pricing.no_price", "No price list, agreement or list price prices this item for this customer on this date.");
                return false;
            }

            line.Steps.Add(Step(PriceStepKinds.BasePrice, quote.Source, quote.RefType, quote.RefId, quote.RefCode, null, quote.Price, null, quote.Facts, candidates));
            line.Steps.AddRange(quote.Trail);
        }

        line.Source = quote.Source;
        line.IncludesTax = quote.IncludesTax ?? _documentBasis;
        if (quote.IncludesTax is { } basis)
        {
            _documentBasis ??= basis;
            if (basis != _documentBasis)
            {
                // A-148: a price on the other basis is converted at the line's tax rate: gross = net × (100 + rate) ÷ 100.
                if (_taxRates.GetValueOrDefault(line.ItemId) is not { } tax)
                {
                    line.Fail("pricing.tax_basis_mismatch", basis
                        ? "The price found includes tax but the document's prices exclude it, and the line has no tax rate to convert it with."
                        : "The price found excludes tax but the document's prices include it, and the line has no tax rate to convert it with.");
                    return false;
                }

                var converted = basis
                    ? _ctx.Rounding.Round(quote.Price * 100m / (100m + tax.RatePct), PriceDecimals)
                    : _ctx.Rounding.Round(quote.Price * (100m + tax.RatePct) / 100m, PriceDecimals);
                var facts = new List<PriceFact> { Fact("fromBasis", basis ? "inclusive" : "exclusive"), Fact("toBasis", basis ? "exclusive" : "inclusive"), Fact("taxRate", tax.RatePct) };
                if (tax.Code is not null)
                {
                    facts.Add(Fact("taxCode", tax.Code));
                }

                line.Steps.Add(Step(PriceStepKinds.TaxBasis, null, null, null, null, quote.Price, converted, null, facts, []));
                quote = quote with { Price = converted, IncludesTax = _documentBasis };
                line.IncludesTax = _documentBasis;
            }
        }

        try
        {
            if (!string.Equals(quote.Currency, _ctx.Currency.Code, StringComparison.Ordinal))
            {
                var rate = RateFor(quote.Currency, _ctx.Currency.Code);
                line.UnitPrice = _ctx.Rounding.Round(quote.Price * rate.Rate, PriceDecimals);
                line.Steps.Add(Step(PriceStepKinds.Currency, null, null, null, null, quote.Price, line.UnitPrice, null,
                    [Fact("from", quote.Currency), Fact("to", _ctx.Currency.Code), Fact("rate", rate.Rate), Fact("rateMethod", rate.Method), Fact("rateDate", rate.EffectiveFrom), Fact("rateType", _ctx.RateType), Fact("decimals", PriceDecimals)], []));
            }
            else
            {
                line.UnitPrice = _ctx.Rounding.Round(quote.Price, PriceDecimals);
                if (line.UnitPrice != quote.Price)
                {
                    line.Steps.Add(Step(PriceStepKinds.Currency, null, null, null, null, quote.Price, line.UnitPrice, null, [Fact("decimals", PriceDecimals)], []));
                }
            }
        }
        catch (MissingRateException missing)
        {
            return RateMissing(line, missing);
        }

        line.Gross = Money(line.Quantity * line.UnitPrice);
        line.Running = line.Gross;
        return true;
    }

    private bool RateMissing(LineState line, MissingRateException missing)
    {
        line.Fail("pricing.rate_missing", $"No {missing.From}→{missing.To} {_ctx.RateType} rate on or before {_ctx.PricingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.");
        return false;
    }

    private Quote? FindBasePrice(LineState line, List<PriceCandidate> candidates)
    {
        var agreement = AgreementPrice(line);
        if (agreement is not null)
        {
            candidates.Add(new PriceCandidate(PriceSources.Agreement, "agreement", agreement.RefId, agreement.RefCode, CandidateOutcomes.Won, agreement.Price));
            return agreement;
        }

        var seen = new HashSet<Guid>();
        foreach (var (source, list) in ListsInOrder())
        {
            if (!seen.Add(list.Id))
            {
                continue;
            }

            if (!list.IsActive)
            {
                candidates.Add(new PriceCandidate(source, "price_list", list.Id, list.Code, CandidateOutcomes.Inactive));
                continue;
            }

            if (!PriceEngine.ValidOn(list.ValidFrom, list.ValidTo, _ctx.PricingDate))
            {
                candidates.Add(new PriceCandidate(source, "price_list", list.Id, list.Code, CandidateOutcomes.NotValid));
                continue;
            }

            var found = PriceFromList(list, line, 0, []);
            if (found is null)
            {
                candidates.Add(new PriceCandidate(source, "price_list", list.Id, list.Code, CandidateOutcomes.NoPrice));
                continue;
            }

            var inLineUnit = InLineUnit(found, line);
            candidates.Add(new PriceCandidate(source, "price_list", list.Id, list.Code, CandidateOutcomes.Won, inLineUnit.Price));
            return inLineUnit with { Source = source };
        }

        var item = line.Item!;
        if (item.ListPrice is { } listPrice)
        {
            var currency = item.ListPriceCurrency ?? _ctx.FunctionalCurrency;
            var perLineUnit = listPrice * line.Uom!.Factor;
            var trail = new List<PriceStep>();
            if (line.Uom.UomId != item.BaseUomId)
            {
                trail.Add(Step(PriceStepKinds.Unit, null, null, null, null, listPrice, perLineUnit, null, [Fact("fromUom", BaseUomCode(item)), Fact("toUom", line.Uom.Code), Fact("factor", line.Uom.Factor)], []));
            }

            candidates.Add(new PriceCandidate(PriceSources.ItemListPrice, "item", item.Id, item.Code, CandidateOutcomes.Won, perLineUnit));
            return new Quote(perLineUnit, currency, false, PriceSources.ItemListPrice, "item", item.Id, item.Code, [Fact("currency", currency), Fact("uom", BaseUomCode(item))], trail);
        }

        candidates.Add(new PriceCandidate(PriceSources.ItemListPrice, "item", item.Id, item.Code, CandidateOutcomes.NoPrice));
        return null;
    }

    /// <summary>The _lists a line may be priced from, in the order of ADR-0030; within a level the lower priority number first, then the code.</summary>
    private IEnumerable<(string Source, EngineList List)> ListsInOrder()
    {
        if (_ctx.DocumentListId is { } documentList && _lists.TryGetValue(documentList, out var named))
        {
            yield return (PriceSources.DocumentList, named);
        }

        if (_ctx.PartnerId is { } partnerId)
        {
            foreach (var list in _rules.Lists.Where(l => l.PartnerIds.Contains(partnerId)).OrderBy(static l => l.Priority).ThenBy(static l => l.Code, StringComparer.Ordinal))
            {
                yield return (PriceSources.CustomerList, list);
            }
        }

        if (_ctx.CustomerGroupId is { } groupId)
        {
            foreach (var list in _rules.Lists.Where(l => l.GroupIds.Contains(groupId)).OrderBy(static l => l.Priority).ThenBy(static l => l.Code, StringComparer.Ordinal))
            {
                yield return (PriceSources.GroupList, list);
            }
        }

        foreach (var list in _rules.Lists.Where(static l => l.IsDefault).OrderBy(static l => l.Priority).ThenBy(static l => l.Code, StringComparer.Ordinal))
        {
            yield return (PriceSources.DefaultList, list);
        }
    }

    /// <summary>
    /// A list's price for the line per the matched entry's unit, in the list's currency: its own entry (the variant's
    /// before the item's; the line's unit, else the base unit; the highest break reached), else its parent's price
    /// adjusted, rounded and surcharged by the list's rule.
    /// </summary>
    private Quote? PriceFromList(EngineList list, LineState line, int depth, HashSet<Guid> visited)
    {
        visited.Add(list.Id);
        var own = _listItems.TryGetValue(list.Id, out var entries)
            ? entries.Where(e => e.ItemId == line.ItemId && PriceEngine.ValidOn(e.ValidFrom, e.ValidTo, _ctx.PricingDate)).ToList()
            : [];
        var entry = MatchEntry(own, line, static e => e.VariantId, static e => e.UomId, static e => e.MinQuantity, static e => e.ValidFrom);
        if (entry is not null)
        {
            var facts = new List<PriceFact> { Fact("currency", list.Currency), Fact("uom", UomCode(line.Item!, entry.UomId)), Fact("minQuantity", entry.MinQuantity) };
            if (entry.VariantId is not null)
            {
                facts.Add(Fact("variant", "true"));
            }

            if (entry.ValidFrom is { } from)
            {
                facts.Add(Fact("validFrom", from));
            }

            facts.Add(Fact("taxBasis", list.PricesIncludeTax ? "inclusive" : "exclusive"));
            return new Quote(entry.Price, list.Currency, list.PricesIncludeTax, string.Empty, "price_list", list.Id, list.Code, facts, [], entry.UomId);
        }

        if (list.ParentId is not { } parentId || depth >= MaxDerivationDepth || visited.Contains(parentId) || !_lists.TryGetValue(parentId, out var parent)
            || !parent.IsActive || !PriceEngine.ValidOn(parent.ValidFrom, parent.ValidTo, _ctx.PricingDate))
        {
            return null;
        }

        var inherited = PriceFromList(parent, line, depth + 1, visited);
        if (inherited is null)
        {
            return null;
        }

        var inListCurrency = inherited.Price;
        var derivationFacts = new List<PriceFact> { Fact("parentList", parent.Code) };
        if (!string.Equals(parent.Currency, list.Currency, StringComparison.Ordinal))
        {
            var rate = RateFor(parent.Currency, list.Currency);
            inListCurrency = inherited.Price * rate.Rate;
            derivationFacts.Add(Fact("from", parent.Currency));
            derivationFacts.Add(Fact("to", list.Currency));
            derivationFacts.Add(Fact("rate", rate.Rate));
            derivationFacts.Add(Fact("rateDate", rate.EffectiveFrom));
        }

        var adjustment = list.AdjustmentPct ?? 0m;
        var adjusted = inListCurrency * (1m + (adjustment / 100m));
        derivationFacts.Add(Fact("adjustmentPct", adjustment));
        var rounded = RoundToRule(adjusted, list);
        if (list.RoundingIncrement is { } increment)
        {
            derivationFacts.Add(Fact("roundingIncrement", increment));
            derivationFacts.Add(Fact("roundingMode", list.RoundingMode));
        }

        var price = Math.Max(0m, rounded + list.Surcharge);
        if (list.Surcharge != 0m)
        {
            derivationFacts.Add(Fact("surcharge", list.Surcharge));
        }

        var trail = new List<PriceStep>(inherited.Trail)
        {
            Step(PriceStepKinds.Derivation, null, "price_list", list.Id, list.Code, inherited.Price, price, null, derivationFacts, []),
        };
        var ownFacts = new List<PriceFact> { Fact("currency", list.Currency), Fact("uom", UomCode(line.Item!, inherited.EntryUomId!.Value)), Fact("derivedFrom", parent.Code), Fact("taxBasis", list.PricesIncludeTax ? "inclusive" : "exclusive") };
        return new Quote(price, list.Currency, list.PricesIncludeTax, string.Empty, "price_list", list.Id, list.Code, ownFacts, trail, inherited.EntryUomId);
    }

    /// <summary>A price per the matched entry's unit converted to the line's unit (exactly: price × line factor ÷ entry factor).</summary>
    private static Quote InLineUnit(Quote quote, LineState line)
    {
        if (quote.EntryUomId is not { } entryUom || entryUom == line.Uom!.UomId)
        {
            return quote;
        }

        var item = line.Item!;
        var entryFactor = item.Uoms.First(u => u.UomId == entryUom).Factor;
        var price = quote.Price * line.Uom.Factor / entryFactor;
        var trail = new List<PriceStep>(quote.Trail)
        {
            Step(PriceStepKinds.Unit, null, null, null, null, quote.Price, price, null, [Fact("fromUom", UomCode(item, entryUom)), Fact("toUom", line.Uom.Code), Fact("factor", line.Uom.Factor / entryFactor)], []),
        };
        return quote with { Price = price, Trail = trail, EntryUomId = line.Uom.UomId };
    }

    private decimal RoundToRule(decimal value, EngineList list) =>
        list.RoundingIncrement is { } increment ? _ctx.Rounding.RoundToMultiple(value, increment, PriceEngine.Direction(list.RoundingMode)) : _ctx.Rounding.Round(value, 10);

    /// <summary>
    /// The entry that prices a line: the variant's entries before the item's; within them the line's unit (break on the
    /// line's quantity) before the base unit (break on the base quantity); the highest break reached, then the latest start.
    /// </summary>
    private static T? MatchEntry<T>(IReadOnlyList<T> entries, LineState line, Func<T, Guid?> variantOf, Func<T, Guid?> uomOf, Func<T, decimal> minOf, Func<T, DateOnly?> fromOf)
        where T : class
    {
        var item = line.Item!;
        var pools = line.VariantId is { } variantId
            ? new[] { entries.Where(e => variantOf(e) == variantId).ToList(), entries.Where(e => variantOf(e) is null).ToList() }
            : new[] { entries.Where(e => variantOf(e) is null).ToList() };
        foreach (var pool in pools)
        {
            var exact = pool.Where(e => uomOf(e) == line.Uom!.UomId && minOf(e) <= line.Quantity).OrderByDescending(minOf).ThenByDescending(e => fromOf(e) ?? DateOnly.MinValue).FirstOrDefault();
            if (exact is not null)
            {
                return exact;
            }

            if (line.Uom!.UomId != item.BaseUomId)
            {
                var inBase = pool.Where(e => uomOf(e) == item.BaseUomId && minOf(e) <= line.BaseQuantity).OrderByDescending(minOf).ThenByDescending(e => fromOf(e) ?? DateOnly.MinValue).FirstOrDefault();
                if (inBase is not null)
                {
                    return inBase;
                }
            }
        }

        return null;
    }

    private Quote? AgreementPrice(LineState line)
    {
        var agreements = _rules.Agreements.Where(a => a.IsActive && a.Price is not null && a.ItemId == line.ItemId && PriceEngine.ValidOn(a.ValidFrom, a.ValidTo, _ctx.PricingDate)).ToList();
        var agreement = MatchEntry(agreements, line, static a => a.VariantId, static a => a.UomId, static a => a.MinQuantity, static a => a.ValidFrom);
        if (agreement is null)
        {
            return null;
        }

        var facts = new List<PriceFact> { Fact("currency", agreement.Currency!), Fact("uom", UomCode(line.Item!, agreement.UomId!.Value)), Fact("minQuantity", agreement.MinQuantity) };
        if (agreement.ValidFrom is { } from)
        {
            facts.Add(Fact("validFrom", from));
        }

        if (agreement.ValidTo is { } to)
        {
            facts.Add(Fact("validTo", to));
        }

        var quote = new Quote(agreement.Price!.Value, agreement.Currency!, false, PriceSources.Agreement, "agreement", agreement.Id, agreement.Reference, facts, [], agreement.UomId);
        return InLineUnit(quote, line);
    }

    /// <summary>Step 3: the best exclusive discount (agreement or rule) on the gross, then stackable _rules in priority order on the running net, then a manual discount.</summary>
    private void ApplyLineDiscounts(LineState line)
    {
        var item = line.Item!;
        var exclusive = new List<Offer>();
        var agreement = AgreementDiscount(line);
        if (agreement is not null)
        {
            exclusive.Add(new Offer(PriceSources.Agreement, "agreement", agreement.Id, agreement.Reference, 0, 10_000, null, string.Empty, DiscountValueTypes.Percentage, agreement.DiscountPct!.Value, null));
        }

        var stackable = new List<Offer>();
        var considered = new List<PriceCandidate>();
        foreach (var rule in _rules.DiscountRules.Where(r => r.Level == DiscountLevels.Line && r.IsActive && PriceEngine.ValidOn(r.ValidFrom, r.ValidTo, _ctx.PricingDate)))
        {
            var specificity = LineScope(rule, item);
            if (specificity is null)
            {
                continue;
            }

            var unmet = LineConditions(rule, line);
            if (unmet is not null)
            {
                considered.Add(new PriceCandidate("rule", "discount_rule", rule.Id, rule.Code, CandidateOutcomes.ConditionNotMet, null, unmet));
                continue;
            }

            var offer = new Offer("rule", "discount_rule", rule.Id, rule.Code, rule.Priority, specificity.Value, rule.ValidFrom, rule.Code, rule.ValueType, rule.Value, rule.Currency);
            (rule.Combination == Combinations.Exclusive ? exclusive : stackable).Add(offer);
        }

        var evaluated = new List<(Offer Offer, decimal Amount)>();
        foreach (var offer in exclusive)
        {
            try
            {
                evaluated.Add((offer, LineOfferAmount(offer, line, line.Gross)));
            }
            catch (MissingRateException missing)
            {
                considered.Add(new PriceCandidate(offer.Source, offer.RefType, offer.RefId, offer.RefCode, CandidateOutcomes.Skipped, null, $"rate_missing:{missing.From}>{missing.To}"));
            }
        }

        // The near misses are listed by code, whatever order the _rules were read in.
        considered.Sort(static (x, y) => string.CompareOrdinal(x.RefCode, y.RefCode));
        var ranked = evaluated
            .OrderByDescending(static e => e.Amount)
            .ThenBy(static e => e.Offer.Priority)
            .ThenByDescending(static e => e.Offer.Specificity)
            .ThenBy(static e => e.Offer.ValidFrom ?? DateOnly.MinValue)
            .ThenBy(static e => e.Offer.TieKey, StringComparer.Ordinal)
            .ToList();
        if (ranked.Count > 0 && ranked[0].Amount > 0m)
        {
            var (winner, amount) = ranked[0];
            var candidates = ranked.Skip(1).Select(static e => new PriceCandidate(e.Offer.Source, e.Offer.RefType, e.Offer.RefId, e.Offer.RefCode, e.Amount > 0m ? CandidateOutcomes.Lost : CandidateOutcomes.NoSaving, e.Amount)).ToList();
            candidates.Insert(0, new PriceCandidate(winner.Source, winner.RefType, winner.RefId, winner.RefCode, CandidateOutcomes.Won, amount));
            candidates.AddRange(considered);
            considered.Clear();
            TakeLineDiscount(line, winner, amount, Combinations.Exclusive, candidates);
        }
        else if (ranked.Count > 0)
        {
            considered.AddRange(ranked.Select(static e => new PriceCandidate(e.Offer.Source, e.Offer.RefType, e.Offer.RefId, e.Offer.RefCode, CandidateOutcomes.NoSaving, 0m)));
        }

        foreach (var offer in stackable.OrderBy(static o => o.Priority).ThenByDescending(static o => o.Specificity).ThenBy(static o => o.TieKey, StringComparer.Ordinal))
        {
            decimal amount;
            try
            {
                amount = LineOfferAmount(offer, line, line.Running);
            }
            catch (MissingRateException missing)
            {
                considered.Add(new PriceCandidate(offer.Source, offer.RefType, offer.RefId, offer.RefCode, CandidateOutcomes.Skipped, null, $"rate_missing:{missing.From}>{missing.To}"));
                continue;
            }

            if (amount <= 0m)
            {
                considered.Add(new PriceCandidate(offer.Source, offer.RefType, offer.RefId, offer.RefCode, CandidateOutcomes.NoSaving, 0m));
                continue;
            }

            var candidates = new List<PriceCandidate> { new(offer.Source, offer.RefType, offer.RefId, offer.RefCode, CandidateOutcomes.Applied, amount) };
            candidates.AddRange(considered);
            considered.Clear();
            TakeLineDiscount(line, offer, amount, Combinations.Stackable, candidates);
        }

        if (line.ManualDiscountPct is { } manualPct && manualPct > 0m)
        {
            var offer = new Offer(PriceSources.Manual, null, null, null, int.MaxValue, 0, null, string.Empty, DiscountValueTypes.Percentage, Math.Min(manualPct, 100m), null);
            var amount = LineOfferAmount(offer, line, line.Running);
            var candidates = new List<PriceCandidate> { new(PriceSources.Manual, null, null, null, CandidateOutcomes.Applied, amount) };
            candidates.AddRange(considered);
            considered.Clear();
            TakeLineDiscount(line, offer, amount, Combinations.Stackable, candidates);
        }

        if (considered.Count > 0)
        {
            // Rules that matched the line but gave nothing are still part of its explanation.
            line.Steps.Add(Step(PriceStepKinds.LineDiscount, null, null, null, null, line.Running, line.Running, 0m, [], considered));
        }
    }

    private static void TakeLineDiscount(LineState line, Offer offer, decimal amount, string combination, List<PriceCandidate> candidates)
    {
        var before = line.Running;
        line.Running -= amount;
        line.LineDiscount += amount;
        var facts = new List<PriceFact> { Fact("valueType", offer.ValueType), Fact("value", offer.Value), Fact("combination", combination) };
        if (offer.Currency is { } currency)
        {
            facts.Add(Fact("currency", currency));
        }

        line.Steps.Add(Step(PriceStepKinds.LineDiscount, offer.Source, offer.RefType, offer.RefId, offer.RefCode, before, line.Running, amount, facts, candidates));
    }

    /// <summary>What an offer takes off an amount of the line: a percentage of it; an amount per base unit; or down to a fixed price per base unit.</summary>
    private decimal LineOfferAmount(Offer offer, LineState line, decimal onAmount)
    {
        var amount = offer.ValueType switch
        {
            DiscountValueTypes.Percentage => Money(onAmount * offer.Value / 100m),
            DiscountValueTypes.Amount => Money(Convert(offer.Value, offer.Currency) * line.BaseQuantity),
            DiscountValueTypes.FixedPrice => onAmount - Money(Convert(offer.Value, offer.Currency) * line.BaseQuantity),
            _ => 0m,
        };
        return Math.Clamp(amount, 0m, onAmount);
    }

    private EngineAgreement? AgreementDiscount(LineState line)
    {
        var item = line.Item!;
        return _rules.Agreements
            .Where(a => a.IsActive && a.DiscountPct is not null && PriceEngine.ValidOn(a.ValidFrom, a.ValidTo, _ctx.PricingDate) && a.MinQuantity <= line.BaseQuantity)
            .Select(a => (Agreement: a, Rank: AgreementRank(a, item, line.VariantId)))
            .Where(static x => x.Rank is not null)
            .OrderByDescending(static x => x.Rank!.Value)
            .ThenByDescending(static x => x.Agreement.MinQuantity)
            .ThenByDescending(static x => x.Agreement.ValidFrom ?? DateOnly.MinValue)
            .Select(static x => x.Agreement)
            .FirstOrDefault();
    }

    /// <summary>A variant agreement over its item's, an item's over a category's, a nearer category over a farther one; null when it does not apply.</summary>
    private static int? AgreementRank(EngineAgreement agreement, EngineItem item, Guid? variantId)
    {
        if (agreement.ItemId is { } itemId)
        {
            if (itemId != item.Id)
            {
                return null;
            }

            return agreement.VariantId is null ? 1_000 : agreement.VariantId == variantId ? 2_000 : null;
        }

        var distance = agreement.CategoryId is { } category ? IndexOf(item.CategoryLineage, category) : -1;
        return distance < 0 ? null : 500 - distance;
    }

    /// <summary>How specific a line rule is when every scope it sets matches the item and the document; null when one does not.</summary>
    private int? LineScope(EngineDiscountRule rule, EngineItem item)
    {
        var score = DocumentScope(rule.PartnerId, rule.CustomerGroupId, rule.Channel, rule.PaymentTermsId);
        if (score is null || (rule.ItemId is { } itemId && itemId != item.Id) || (rule.BrandId is { } brand && brand != item.BrandId))
        {
            return null;
        }

        if (rule.CategoryId is { } category)
        {
            var distance = IndexOf(item.CategoryLineage, category);
            if (distance < 0)
            {
                return null;
            }

            score += 500 - distance;
        }

        return score + (rule.ItemId is null ? 0 : 1_000) + (rule.BrandId is null ? 0 : 50);
    }

    private int? DocumentScope(Guid? partnerId, Guid? groupId, string? channel, Guid? paymentTermsId)
    {
        if ((partnerId is not null && partnerId != _ctx.PartnerId)
            || (groupId is not null && groupId != _ctx.CustomerGroupId)
            || (channel is not null && !string.Equals(channel, _ctx.Channel, StringComparison.OrdinalIgnoreCase))
            || (paymentTermsId is not null && paymentTermsId != _ctx.PaymentTermsId))
        {
            return null;
        }

        return (partnerId is null ? 0 : 20) + (groupId is null ? 0 : 10) + (channel is null ? 0 : 2) + (paymentTermsId is null ? 0 : 1);
    }

    private string? LineConditions(EngineDiscountRule rule, LineState line)
    {
        if (rule.MinQuantity is { } minQuantity && line.BaseQuantity < minQuantity)
        {
            return $"min_quantity:{PriceEngine.Format(minQuantity)}";
        }

        if (rule.Weekdays is { Count: > 0 } weekdays && !weekdays.Contains((int)_ctx.PricingDate.DayOfWeek))
        {
            return "weekday";
        }

        if (rule.MinAmount is { } minAmount)
        {
            try
            {
                if (line.Gross < Convert(minAmount, rule.Currency))
                {
                    return $"min_amount:{PriceEngine.Format(minAmount)} {rule.Currency}";
                }
            }
            catch (MissingRateException missing)
            {
                return $"rate_missing:{missing.From}>{missing.To}";
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ promotions over the basket

    private void ApplyPromotions()
    {
        var considered = new List<PriceCandidate>();
        var eligible = new List<EnginePromotion>();
        foreach (var promotion in _rules.Promotions.Where(p => p.IsActive && PriceEngine.ValidOn(p.ValidFrom, p.ValidTo, _ctx.PricingDate)).OrderBy(static p => p.Code, StringComparer.Ordinal))
        {
            if (DocumentScope(promotion.PartnerId, promotion.CustomerGroupId, promotion.Channel, null) is null)
            {
                continue;
            }

            if (promotion.CouponCode is { } coupon && !_ctx.CouponCodes.Contains(coupon.ToUpperInvariant(), StringComparer.Ordinal))
            {
                considered.Add(new PriceCandidate(promotion.Kind, "promotion", promotion.Id, promotion.Code, CandidateOutcomes.CouponMissing));
                continue;
            }

            if ((promotion.UsageLimit is { } limit && promotion.Used >= limit)
                || (promotion.UsageLimitPerCustomer is { } perCustomer && _ctx.PartnerId is not null && promotion.UsedByCustomer >= perCustomer))
            {
                considered.Add(new PriceCandidate(promotion.Kind, "promotion", promotion.Id, promotion.Code, CandidateOutcomes.UsageLimitReached));
                continue;
            }

            eligible.Add(promotion);
        }

        var outcomes = new Dictionary<Guid, PriceCandidate>();

        // Exclusive promotions compete: the greatest benefit to the customer is _applied first, and the rest are
        // evaluated again on the _lines no promotion has taken, until none gives anything more.
        var open = eligible.Where(static p => p.Combination == Combinations.Exclusive).ToList();
        while (open.Count > 0)
        {
            var best = open
                .Select(p => (Promotion: p, Application: Evaluate(p, outcomes)))
                .Where(static x => x.Application is not null)
                .OrderByDescending(static x => x.Application!.Benefit)
                .ThenBy(static x => x.Promotion.Priority)
                .ThenBy(static x => x.Promotion.Code, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best.Application is null)
            {
                break;
            }

            Apply(best.Application, exclusive: true);
            outcomes[best.Promotion.Id] = new PriceCandidate(best.Promotion.Kind, "promotion", best.Promotion.Id, best.Promotion.Code, CandidateOutcomes.Applied, best.Application.Benefit);
            open.Remove(best.Promotion);
        }

        foreach (var promotion in eligible.Where(static p => p.Combination == Combinations.Stackable).OrderBy(static p => p.Priority).ThenBy(static p => p.Code, StringComparer.Ordinal))
        {
            var application = Evaluate(promotion, outcomes);
            if (application is null)
            {
                continue;
            }

            Apply(application, exclusive: false);
            outcomes[promotion.Id] = new PriceCandidate(promotion.Kind, "promotion", promotion.Id, promotion.Code, CandidateOutcomes.Applied, application.Benefit);
        }

        considered.AddRange(eligible.Select(p => outcomes.GetValueOrDefault(p.Id) ?? new PriceCandidate(p.Kind, "promotion", p.Id, p.Code, CandidateOutcomes.ConditionNotMet)));
        if (considered.Count > 0)
        {
            _documentSteps.Add(Step(PriceStepKinds.Promotion, null, null, null, null, null, null, _applied.Sum(static a => a.Benefit), [], considered.OrderBy(static c => c.RefCode, StringComparer.Ordinal).ToList()));
        }
    }

    /// <summary>What a promotion would give on the _lines still open to it, or null (with the reason in <paramref name="outcomes"/>) when it gives nothing.</summary>
    private PromotionApplication? Evaluate(EnginePromotion promotion, Dictionary<Guid, PriceCandidate> outcomes)
    {
        var open = _lines.Where(static l => l.Problem is null && !l.IsFree && !l.Exclusive).ToList();
        if (promotion.Combination == Combinations.Exclusive)
        {
            open = open.Where(static l => !l.Promoted).ToList();
        }

        var inScope = open.Where(l => InPromotionScope(promotion, l.Item!)).ToList();
        var taken = inScope.Count == 0 && _lines.Any(l => l.Problem is null && !l.IsFree && InPromotionScope(promotion, l.Item!));
        PriceCandidate NotMet(string detail) => outcomes[promotion.Id] = new PriceCandidate(promotion.Kind, "promotion", promotion.Id, promotion.Code, taken ? CandidateOutcomes.LineTaken : CandidateOutcomes.ConditionNotMet, null, taken ? null : detail);
        try
        {
            switch (promotion.Kind)
            {
                case PromotionKinds.Coupon:
                    {
                        var effects = inScope.Select(l => new PromotionEffect(l, Money(l.Running * promotion.DiscountPct!.Value / 100m))).Where(static e => e.Amount > 0m).ToList();
                        if (effects.Count == 0)
                        {
                            NotMet("no_line_in_scope");
                            return null;
                        }

                        return new PromotionApplication(promotion, effects, inScope, null, [Fact("discountPct", promotion.DiscountPct!.Value)]);
                    }

                case PromotionKinds.VolumeTier:
                    {
                        var total = inScope.Sum(static l => l.BaseQuantity);
                        var tier = promotion.Tiers.Where(t => t.MinQuantity <= total).OrderByDescending(static t => t.MinQuantity).FirstOrDefault();
                        if (tier is null)
                        {
                            var first = promotion.Tiers.Min(static t => t.MinQuantity);
                            NotMet($"volume:{PriceEngine.Format(total)}/{PriceEngine.Format(first)}");
                            return null;
                        }

                        var effects = inScope.Select(l => new PromotionEffect(l, Money(l.Running * tier.DiscountPct / 100m))).Where(static e => e.Amount > 0m).ToList();
                        return effects.Count == 0 ? null : new PromotionApplication(promotion, effects, inScope, null, [Fact("quantity", total), Fact("tier", tier.MinQuantity), Fact("discountPct", tier.DiscountPct)]);
                    }

                case PromotionKinds.Bundle:
                    return EvaluateBundle(promotion, open, NotMet);

                case PromotionKinds.BuyXGetY:
                    {
                        var bought = inScope.Sum(static l => l.BaseQuantity);
                        var applications = RoundingPolicy.WholeTimes(bought, promotion.BuyQuantity!.Value);
                        if (promotion.MaxApplications is { } max)
                        {
                            applications = Math.Min(applications, max);
                        }

                        if (applications < 1m)
                        {
                            NotMet($"buy:{PriceEngine.Format(bought)}/{PriceEngine.Format(promotion.BuyQuantity.Value)}");
                            return null;
                        }

                        var getItemId = promotion.GetItemId ?? promotion.ItemId!.Value;
                        if (!input.Items.TryGetValue(getItemId, out var getItem) || !getItem.IsActive)
                        {
                            NotMet("get_item_unavailable");
                            return null;
                        }

                        var free = new LineState
                        {
                            Key = $"{promotion.Code}#free",
                            Item = getItem,
                            ItemId = getItem.Id,
                            Uom = getItem.Uoms.First(u => u.UomId == getItem.BaseUomId),
                            Quantity = applications * promotion.GetQuantity!.Value,
                            BaseQuantity = applications * promotion.GetQuantity.Value,
                            IsFree = true,
                            PromotionId = promotion.Id,
                            PromotionCode = promotion.Code,
                            FreeForKey = inScope[0].Key,
                        };
                        if (!ResolveUnitPrice(free, null))
                        {
                            // Goods given away still go out with the promotion; without a regular price their value is nil.
                            free.Problem = null;
                            free.UnitPrice = 0m;
                            free.Gross = 0m;
                            free.Running = 0m;
                        }

                        var discount = Money(free.Gross * promotion.GetDiscountPct!.Value / 100m);
                        return new PromotionApplication(promotion, [], inScope, (free, discount),
                            [Fact("bought", bought), Fact("buyQuantity", promotion.BuyQuantity.Value), Fact("applications", applications), Fact("freeQuantity", free.BaseQuantity), Fact("discountPct", promotion.GetDiscountPct.Value)]);
                    }

                default:
                    return null;
            }
        }
        catch (MissingRateException missing)
        {
            outcomes[promotion.Id] = new PriceCandidate(promotion.Kind, "promotion", promotion.Id, promotion.Code, CandidateOutcomes.Skipped, null, $"rate_missing:{missing.From}>{missing.To}");
            return null;
        }
    }

    private PromotionApplication? EvaluateBundle(EnginePromotion promotion, List<LineState> open, Func<string, PriceCandidate> notMet)
    {
        var components = promotion.Components.OrderBy(static c => c.ItemId).ToList();
        if (components.Count == 0)
        {
            notMet("no_components");
            return null;
        }

        var sets = decimal.MaxValue;
        foreach (var component in components)
        {
            var available = open.Where(l => l.ItemId == component.ItemId).Sum(static l => l.BaseQuantity);
            sets = Math.Min(sets, RoundingPolicy.WholeTimes(available, component.Quantity));
        }

        if (sets < 1m)
        {
            notMet("bundle_incomplete");
            return null;
        }

        var draws = new List<(LineState Line, decimal Value)>();
        foreach (var component in components)
        {
            var need = sets * component.Quantity;
            foreach (var line in open.Where(l => l.ItemId == component.ItemId))
            {
                if (need <= 0m)
                {
                    break;
                }

                var take = Math.Min(need, line.BaseQuantity);
                need -= take;
                draws.Add((line, Money(line.Running * take / line.BaseQuantity)));
            }
        }

        var regular = draws.Sum(static d => d.Value);
        var bundleTotal = Money(Convert(promotion.BundlePrice!.Value, promotion.Currency) * sets);
        var discount = regular - bundleTotal;
        if (discount <= 0m)
        {
            notMet("no_saving");
            return null;
        }

        var shares = _ctx.Rounding.Allocate(new Money(discount, _ctx.Currency), draws.Select(static d => d.Value).ToList());
        var effects = draws.Select((d, i) => new PromotionEffect(d.Line, shares[i].Amount))
            .GroupBy(static e => e.Line)
            .Select(static g => new PromotionEffect(g.Key, g.Sum(static e => e.Amount)))
            .Where(static e => e.Amount > 0m)
            .ToList();
        return new PromotionApplication(promotion, effects, draws.Select(static d => d.Line).Distinct().ToList(), null,
            [Fact("sets", sets), Fact("regularValue", regular), Fact("bundlePrice", promotion.BundlePrice.Value), Fact("currency", promotion.Currency!), Fact("bundleTotal", bundleTotal)]);
    }

    private void Apply(PromotionApplication application, bool exclusive)
    {
        var promotion = application.Promotion;
        foreach (var line in application.Consumed)
        {
            line.Promoted = true;
            line.Exclusive |= exclusive;
        }

        var keys = new List<string>();
        foreach (var effect in application.Effects)
        {
            var line = effect.Line;
            var before = line.Running;
            line.Running -= effect.Amount;
            line.PromotionDiscount += effect.Amount;
            line.PromotionId ??= promotion.Id;
            line.PromotionCode ??= promotion.Code;
            line.Steps.Add(Step(PriceStepKinds.Promotion, promotion.Kind, "promotion", promotion.Id, promotion.Code, before, line.Running, effect.Amount, [.. application.Facts, Fact("combination", promotion.Combination)], []));
            keys.Add(line.Key);
        }

        var benefit = application.Effects.Sum(static e => e.Amount);
        if (application.Free is var (free, discount))
        {
            var before = free.Running;
            free.Running -= discount;
            free.PromotionDiscount = discount;
            free.Steps.Add(Step(PriceStepKinds.Promotion, promotion.Kind, "promotion", promotion.Id, promotion.Code, before, free.Running, discount, [.. application.Facts, Fact("combination", promotion.Combination)], []));
            var after = _lines.FindLastIndex(l => l.Key == free.FreeForKey || (l.IsFree && l.FreeForKey == free.FreeForKey));
            _lines.Insert(after + 1, free);
            keys.Add(free.Key);
            benefit += discount;
        }

        _applied.Add(new AppliedPromotion(promotion.Id, promotion.Code, promotion.Kind, promotion.Combination, benefit, keys));
    }

    private static bool InPromotionScope(EnginePromotion promotion, EngineItem item) =>
        (promotion.ItemId is null || promotion.ItemId == item.Id)
        && (promotion.CategoryId is null || item.CategoryLineage.Contains(promotion.CategoryId.Value))
        && (promotion.BrandId is null || promotion.BrandId == item.BrandId);

    // ------------------------------------------------------------------ document discounts

    /// <summary>Step 5: document _rules and a manual document discount on the subtotal, each allocated to the _lines by largest remainder.</summary>
    private void ApplyDocumentDiscounts()
    {
        var priced = _lines.Where(static l => l.Problem is null).ToList();
        var subtotal = priced.Sum(static l => l.Running);
        var considered = new List<PriceCandidate>();
        var exclusive = new List<(Offer Offer, decimal Amount)>();
        var stackable = new List<Offer>();
        foreach (var rule in _rules.DiscountRules.Where(r => r.Level == DiscountLevels.Document && r.IsActive && PriceEngine.ValidOn(r.ValidFrom, r.ValidTo, _ctx.PricingDate)))
        {
            var specificity = DocumentScope(rule.PartnerId, rule.CustomerGroupId, rule.Channel, rule.PaymentTermsId);
            if (specificity is null)
            {
                continue;
            }

            var offer = new Offer("rule", "discount_rule", rule.Id, rule.Code, rule.Priority, specificity.Value, rule.ValidFrom, rule.Code, rule.ValueType, rule.Value, rule.Currency);
            try
            {
                if (rule.Weekdays is { Count: > 0 } weekdays && !weekdays.Contains((int)_ctx.PricingDate.DayOfWeek))
                {
                    considered.Add(new PriceCandidate("rule", "discount_rule", rule.Id, rule.Code, CandidateOutcomes.ConditionNotMet, null, "weekday"));
                    continue;
                }

                if (rule.MinAmount is { } minAmount && subtotal < Convert(minAmount, rule.Currency))
                {
                    considered.Add(new PriceCandidate("rule", "discount_rule", rule.Id, rule.Code, CandidateOutcomes.ConditionNotMet, null, $"min_amount:{PriceEngine.Format(minAmount)} {rule.Currency}"));
                    continue;
                }

                if (rule.Combination == Combinations.Exclusive)
                {
                    exclusive.Add((offer, DocumentOfferAmount(offer, subtotal)));
                }
                else
                {
                    stackable.Add(offer);
                }
            }
            catch (MissingRateException missing)
            {
                considered.Add(new PriceCandidate("rule", "discount_rule", rule.Id, rule.Code, CandidateOutcomes.Skipped, null, $"rate_missing:{missing.From}>{missing.To}"));
            }
        }

        considered.Sort(static (x, y) => string.CompareOrdinal(x.RefCode, y.RefCode));
        var ranked = exclusive
            .OrderByDescending(static e => e.Amount)
            .ThenBy(static e => e.Offer.Priority)
            .ThenByDescending(static e => e.Offer.Specificity)
            .ThenBy(static e => e.Offer.ValidFrom ?? DateOnly.MinValue)
            .ThenBy(static e => e.Offer.TieKey, StringComparer.Ordinal)
            .ToList();
        if (ranked.Count > 0 && ranked[0].Amount > 0m)
        {
            var candidates = new List<PriceCandidate> { new(ranked[0].Offer.Source, ranked[0].Offer.RefType, ranked[0].Offer.RefId, ranked[0].Offer.RefCode, CandidateOutcomes.Won, ranked[0].Amount) };
            candidates.AddRange(ranked.Skip(1).Select(static e => new PriceCandidate(e.Offer.Source, e.Offer.RefType, e.Offer.RefId, e.Offer.RefCode, e.Amount > 0m ? CandidateOutcomes.Lost : CandidateOutcomes.NoSaving, e.Amount)));
            candidates.AddRange(considered);
            considered.Clear();
            subtotal = TakeDocumentDiscount(priced, ranked[0].Offer, ranked[0].Amount, subtotal, Combinations.Exclusive, candidates);
        }

        foreach (var offer in stackable.OrderBy(static o => o.Priority).ThenByDescending(static o => o.Specificity).ThenBy(static o => o.TieKey, StringComparer.Ordinal))
        {
            var amount = DocumentOfferAmount(offer, subtotal);
            if (amount <= 0m)
            {
                continue;
            }

            var candidates = new List<PriceCandidate> { new(offer.Source, offer.RefType, offer.RefId, offer.RefCode, CandidateOutcomes.Applied, amount) };
            candidates.AddRange(considered);
            considered.Clear();
            subtotal = TakeDocumentDiscount(priced, offer, amount, subtotal, Combinations.Stackable, candidates);
        }

        if (_ctx.DocumentDiscountPct is { } manualPct && manualPct > 0m)
        {
            var offer = new Offer(PriceSources.Manual, null, null, null, int.MaxValue, 0, null, string.Empty, DiscountValueTypes.Percentage, Math.Min(manualPct, 100m), null);
            var amount = DocumentOfferAmount(offer, subtotal);
            if (amount > 0m)
            {
                var candidates = new List<PriceCandidate> { new(PriceSources.Manual, null, null, null, CandidateOutcomes.Applied, amount) };
                candidates.AddRange(considered);
                considered.Clear();
                TakeDocumentDiscount(priced, offer, amount, subtotal, Combinations.Stackable, candidates);
            }
        }

        if (considered.Count > 0)
        {
            _documentSteps.Add(Step(PriceStepKinds.DocumentDiscount, null, null, null, null, subtotal, subtotal, 0m, [], considered));
        }
    }

    private decimal DocumentOfferAmount(Offer offer, decimal subtotal)
    {
        var amount = offer.ValueType == DiscountValueTypes.Amount ? Money(Convert(offer.Value, offer.Currency)) : Money(subtotal * offer.Value / 100m);
        return Math.Clamp(amount, 0m, subtotal);
    }

    private decimal TakeDocumentDiscount(List<LineState> priced, Offer offer, decimal amount, decimal subtotal, string combination, List<PriceCandidate> candidates)
    {
        var weights = priced.Select(static l => l.Running).ToList();
        if (weights.All(static w => w <= 0m))
        {
            return subtotal;
        }

        var shares = _ctx.Rounding.Allocate(new Money(amount, _ctx.Currency), weights);
        var facts = new List<PriceFact> { Fact("valueType", offer.ValueType), Fact("value", offer.Value), Fact("combination", combination), Fact("subtotal", subtotal) };
        if (offer.Currency is { } currency)
        {
            facts.Add(Fact("currency", currency));
        }

        for (var i = 0; i < priced.Count; i++)
        {
            var share = shares[i].Amount;
            if (share == 0m)
            {
                continue;
            }

            var line = priced[i];
            var before = line.Running;
            line.Running -= share;
            line.DocumentDiscount += share;
            line.Steps.Add(Step(PriceStepKinds.DocumentDiscount, offer.Source, offer.RefType, offer.RefId, offer.RefCode, before, line.Running, share, facts, []));
        }

        _documentSteps.Add(Step(PriceStepKinds.DocumentDiscount, offer.Source, offer.RefType, offer.RefId, offer.RefCode, subtotal, subtotal - amount, amount, facts, candidates));
        return subtotal - amount;
    }

    // ------------------------------------------------------------------ floors

    /// <summary>Step 8: the final net per base unit against the item's floor, else its nearest category's: a minimum price and a minimum margin over expected cost.</summary>
    private void CheckFloor(LineState line)
    {
        var item = line.Item!;
        var floor = _rules.Floors.Where(f => f.IsActive && f.ItemId == item.Id).FirstOrDefault()
            ?? _rules.Floors.Where(f => f.IsActive && f.CategoryId is { } c && item.CategoryLineage.Contains(c)).OrderBy(f => IndexOf(item.CategoryLineage, f.CategoryId!.Value)).FirstOrDefault();
        if (floor is null)
        {
            return;
        }

        var net = _ctx.Rounding.Round(line.Running / line.BaseQuantity, PriceDecimals);
        var breached = false;
        string? reason = null;
        decimal? minPrice = null;
        if (floor.MinPrice is { } floorPrice)
        {
            try
            {
                minPrice = _ctx.Rounding.Round(Convert(floorPrice, floor.Currency), PriceDecimals);
                if (net < minPrice)
                {
                    breached = true;
                    reason = "below_min_price";
                }
            }
            catch (MissingRateException missing)
            {
                breached = true;
                reason = $"rate_missing:{missing.From}>{missing.To}";
            }
        }

        decimal? cost = null;
        decimal? margin = null;
        if (floor.MinMarginPct is { } minMargin)
        {
            if (!input.UnitCosts.TryGetValue(item.Id, out var unitCost))
            {
                reason ??= "cost_unknown";
            }
            else
            {
                try
                {
                    cost = _ctx.Rounding.Round(Convert(unitCost, _ctx.FunctionalCurrency), PriceDecimals);
                    margin = net <= 0m ? null : _ctx.Rounding.Round((net - cost.Value) / net * 100m, 4);
                    if (margin is null || margin < minMargin)
                    {
                        breached = true;
                        reason ??= "below_min_margin";
                    }
                }
                catch (MissingRateException missing)
                {
                    breached = true;
                    reason = $"rate_missing:{missing.From}>{missing.To}";
                }
            }
        }

        var scope = floor.ItemId is not null ? "item" : "category";
        line.Floor = new PriceFloorCheck(floor.Id, scope, floor.ItemId ?? floor.CategoryId!.Value, minPrice, floor.MinMarginPct, net, cost, margin, breached, floor.OnBreach, reason);
        var facts = new List<PriceFact> { Fact("netPerBaseUnit", net), Fact("scope", scope), Fact("onBreach", floor.OnBreach) };
        if (minPrice is { } min)
        {
            facts.Add(Fact("minPrice", min));
        }

        if (floor.MinMarginPct is { } minMarginPct)
        {
            facts.Add(Fact("minMarginPct", minMarginPct));
        }

        if (cost is { } c)
        {
            facts.Add(Fact("unitCost", c));
        }

        if (margin is { } m)
        {
            facts.Add(Fact("marginPct", m));
        }

        if (line.IncludesTax == true)
        {
            facts.Add(Fact("taxBasis", "inclusive"));
        }

        line.Steps.Add(Step(PriceStepKinds.Floor, scope, "price_floor", floor.Id, null, null, null, null, facts,
            [new PriceCandidate(scope, "price_floor", floor.Id, null, breached ? CandidateOutcomes.Breached : CandidateOutcomes.Passed, null, reason)]));
    }

    // ------------------------------------------------------------------ helpers

    private PricedLine ToPricedLine(LineState line)
    {
        var item = line.Item;
        var uom = line.Uom;
        var priced = line.Problem is null;
        return new PricedLine(
            line.Key,
            line.ItemId,
            item?.Code ?? string.Empty,
            line.VariantId,
            uom?.UomId ?? Guid.Empty,
            uom?.Code ?? string.Empty,
            line.Quantity,
            line.BaseQuantity,
            line.Source,
            line.IncludesTax,
            priced ? line.UnitPrice : 0m,
            priced ? line.Gross : 0m,
            priced ? line.LineDiscount : 0m,
            priced ? line.PromotionDiscount : 0m,
            priced ? line.DocumentDiscount : 0m,
            priced ? line.Running : 0m,
            priced && line.Quantity > 0m ? _ctx.Rounding.Round(line.Running / line.Quantity, PriceDecimals) : 0m,
            priced && line.Gross > 0m ? _ctx.Rounding.Round((line.Gross - line.Running) / line.Gross * 100m, 4) : 0m,
            line.IsFree,
            line.PromotionId,
            line.PromotionCode,
            line.FreeForKey,
            line.Floor,
            line.Problem,
            line.Steps);
    }

    private decimal Money(decimal amount) => _ctx.Rounding.Round(amount, Minor);

    private decimal Convert(decimal amount, string? currency) =>
        currency is null || string.Equals(currency, _ctx.Currency.Code, StringComparison.Ordinal) ? amount : amount * RateFor(currency, _ctx.Currency.Code).Rate;

    private EngineRate RateFor(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return new EngineRate(1m, "identity", _ctx.PricingDate);
        }

        return input.Rates.TryGetValue($"{from}>{to}", out var rate) ? rate : throw new MissingRateException(from, to);
    }

    private static int IndexOf(IReadOnlyList<Guid> lineage, Guid id)
    {
        for (var i = 0; i < lineage.Count; i++)
        {
            if (lineage[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static string UomCode(EngineItem item, Guid uomId) => item.Uoms.FirstOrDefault(u => u.UomId == uomId)?.Code ?? string.Empty;

    private static string BaseUomCode(EngineItem item) => UomCode(item, item.BaseUomId);

    private static PriceFact Fact(string key, string value) => new(key, value);

    private static PriceFact Fact(string key, decimal value) => new(key, PriceEngine.Format(value));

    private static PriceFact Fact(string key, int value) => new(key, value.ToString(CultureInfo.InvariantCulture));

    private static PriceFact Fact(string key, DateOnly value) => new(key, value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static PriceStep Step(string kind, string? source, string? refType, Guid? refId, string? refCode, decimal? before, decimal? after, decimal? amount, IReadOnlyList<PriceFact> facts, IReadOnlyList<PriceCandidate> candidates) =>
        new(kind, source, refType, refId, refCode, before, after, amount, facts, candidates);

    private sealed class MissingRateException(string from, string to) : Exception($"No {from}→{to} rate.")
    {
        public string From { get; } = from;

        public string To { get; } = to;
    }

    private sealed record Quote(decimal Price, string Currency, bool? IncludesTax, string Source, string? RefType, Guid? RefId, string? RefCode, IReadOnlyList<PriceFact> Facts, IReadOnlyList<PriceStep> Trail, Guid? EntryUomId = null);

    /// <summary>A discount that may apply: its value, how it ranks (priority, specificity, start) and a stable key for the last tie.</summary>
    private sealed record Offer(string Source, string? RefType, Guid? RefId, string? RefCode, int Priority, int Specificity, DateOnly? ValidFrom, string TieKey, string ValueType, decimal Value, string? Currency);

    private sealed record PromotionEffect(LineState Line, decimal Amount);

    private sealed record PromotionApplication(EnginePromotion Promotion, IReadOnlyList<PromotionEffect> Effects, IReadOnlyList<LineState> Consumed, (LineState Line, decimal Discount)? Free, IReadOnlyList<PriceFact> Facts)
    {
        public decimal Benefit => Effects.Sum(static e => e.Amount) + (Free?.Discount ?? 0m);
    }

    private sealed class LineState
    {
        public required string Key { get; init; }

        public Guid ItemId { get; set; }

        public EngineItem? Item { get; set; }

        public Guid? VariantId { get; init; }

        public EngineUom? Uom { get; set; }

        public decimal Quantity { get; init; }

        public decimal BaseQuantity { get; set; }

        public decimal? ManualDiscountPct { get; init; }

        public string? Source { get; set; }

        public bool? IncludesTax { get; set; }

        public decimal UnitPrice { get; set; }

        public decimal Gross { get; set; }

        public decimal Running { get; set; }

        public decimal LineDiscount { get; set; }

        public decimal PromotionDiscount { get; set; }

        public decimal DocumentDiscount { get; set; }

        public bool IsFree { get; init; }

        public bool Promoted { get; set; }

        public bool Exclusive { get; set; }

        public Guid? PromotionId { get; set; }

        public string? PromotionCode { get; set; }

        public string? FreeForKey { get; init; }

        public PriceFloorCheck? Floor { get; set; }

        public PriceProblem? Problem { get; set; }

        public List<PriceStep> Steps { get; } = [];

        public LineState Fail(string code, string message)
        {
            Problem = new PriceProblem(code, message);
            return this;
        }
    }
}
