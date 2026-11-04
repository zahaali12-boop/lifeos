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

    /// <summary>The equity account the owner's money put in is credited to (string, an account code of the company's chart; default 3100, share capital).</summary>
    public const string OwnerCapitalAccount = "trader.owner.capital_account";

    /// <summary>The equity account the owner's money taken out is debited to (string, an account code of the company's chart; default 3600, distributions).</summary>
    public const string OwnerDrawingsAccount = "trader.owner.drawings_account";

    /// <summary>The company's own wording of the WhatsApp reminder of what a customer owes, in English (string; placeholders in braces; the program's default when empty; ADR-0035 §25).</summary>
    public const string WhatsAppReminderEn = "trader.whatsapp.reminder.en";

    /// <summary>The same reminder in Arabic.</summary>
    public const string WhatsAppReminderAr = "trader.whatsapp.reminder.ar";

    /// <summary>The company's own wording of the WhatsApp confirmation of a payment received, in English (string; placeholders in braces; the program's default when empty).</summary>
    public const string WhatsAppReceiptEn = "trader.whatsapp.receipt.en";

    /// <summary>The same confirmation in Arabic.</summary>
    public const string WhatsAppReceiptAr = "trader.whatsapp.receipt.ar";

    /// <summary>The sales price list pricing reads and writes (string, the list's id); the company's default list when empty.</summary>
    public const string PriceListId = "trader.pricing.price_list_id";

    /// <summary>The fixed costs of a month the trader plans for (rent, salaries, utilities), in the company's currency (number); when empty, the average of the last three months' operating expenses stands in.</summary>
    public const string FixedCostsMonthly = "trader.health.fixed_costs_monthly";

    /// <summary>How many days of sales to keep in stock or on the way, for what to reorder (number; default 90).</summary>
    public const string CoverDays = "trader.health.cover_days";

    /// <summary>The hour of the company's day (0–23) from which the day's exchange rate is asked for at the first open, when none is entered for the day; a negative number never asks (number; default 8).</summary>
    public const string RatesPromptHour = "trader.rates.prompt_hour";

    /// <summary>The currencies the trader buys in, asked for daily and shown on the pricing board, as ISO codes separated by commas (string); added to those seen on shipments, and USD when nothing is named or seen.</summary>
    public const string RatesCurrencies = "trader.rates.currencies";
}
