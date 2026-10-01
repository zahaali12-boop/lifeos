namespace Quicker.Accounting.Application;

/// <summary>What a group's value names, so the report can show a code and a name instead of an id.</summary>
public enum GroupingKind
{
    Text,
    Partner,
    User,
    Branch,
    FiscalPeriod,
    TaxCode,
}

/// <summary>
/// One way to group (and filter) journal lines besides a dimension: the SQL expression over the line <c>l</c> and its
/// entry <c>e</c> that yields the group's value as text (null when the line has none), and what that value names.
/// The same expression filters the ledger a grouped figure drills to, so the ledger closes on the figure.
/// </summary>
public sealed record JournalGrouping(string Key, string Sql, GroupingKind Kind);

/// <summary>The built-in groupings of the trial balance (A-156); keys are lower case so they never clash with a dimension code.</summary>
public static class JournalGroupings
{
    public static readonly IReadOnlyList<JournalGrouping> All =
    [
        new("partner", "l.partner_id::text", GroupingKind.Partner),
        new("user", "e.posted_by::text", GroupingKind.User),
        new("date", "to_char(l.posting_date, 'YYYY-MM-DD')", GroupingKind.Text),
        new("week", "to_char(l.posting_date, 'IYYY-\"W\"IW')", GroupingKind.Text),
        new("month", "to_char(l.posting_date, 'YYYY-MM')", GroupingKind.Text),
        new("quarter", "to_char(l.posting_date, 'YYYY-\"Q\"Q')", GroupingKind.Text),
        new("year", "to_char(l.posting_date, 'YYYY')", GroupingKind.Text),
        new("period", "e.fiscal_period_id::text", GroupingKind.FiscalPeriod),
        new("source", "e.source_document_type", GroupingKind.Text),
        new("module", "e.source_module", GroupingKind.Text),
        new("entry", "e.number", GroupingKind.Text),
        new("document", "coalesce(e.source_document_number, e.number)", GroupingKind.Text),
        new("kind", "CASE WHEN e.is_opening_entry THEN 'opening' WHEN e.is_closing_entry THEN 'closing' WHEN e.is_reversal THEN 'reversal' WHEN e.is_manual THEN 'manual' ELSE 'automatic' END", GroupingKind.Text),
        new("currency", "l.currency_tc", GroupingKind.Text),
        new("branch", "l.branch_id::text", GroupingKind.Branch),
        new("subledger", "l.subledger_type", GroupingKind.Text),
        new("tax", "l.tax_code_id::text", GroupingKind.TaxCode),
        new("role", "l.account_role", GroupingKind.Text),
        new("due", "to_char(l.due_date, 'YYYY-MM')", GroupingKind.Text),
    ];

    private static readonly Dictionary<string, JournalGrouping> ByKey = All.ToDictionary(static g => g.Key, StringComparer.Ordinal);

    /// <summary>The filter value that selects lines without a value (a line with no partner, no branch…).</summary>
    public const string None = "~";

    public static JournalGrouping? Find(string key) => ByKey.GetValueOrDefault(key.Trim().ToLowerInvariant());
}
