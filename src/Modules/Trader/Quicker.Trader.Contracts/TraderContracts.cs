namespace Quicker.Trader.Contracts;

/// <summary>
/// Settings of the trader edition, kept in the organization's settings store (tenant-wide, overridable per company)
/// so an admin edits them under Settings like any other setting; the trader's screens edit them in place.
/// </summary>
public static class TraderSettingKeys
{
    /// <summary>The margin pricing aims for, in percent over the cost of a unit today (number; default 30).</summary>
    public const string TargetMarginPct = "trader.pricing.target_margin_pct";

    /// <summary>The increment suggested prices are rounded up to, in the company's currency (number; 0 for none).</summary>
    public const string RoundTo = "trader.pricing.round_to";

    /// <summary>The sales price list pricing reads and writes (string, the list's id); the company's default list when empty.</summary>
    public const string PriceListId = "trader.pricing.price_list_id";
}
