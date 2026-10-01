namespace Quicker.Integrity.Contracts;

/// <summary>The checks the harness runs (roadmap 2.6, ADR-0029); every code is stable and shows in reports and job results.</summary>
public static class InvariantCodes
{
    /// <summary>Every journal entry has the lines it declares and debits equal credits in all three currencies.</summary>
    public const string EntriesBalanced = "entries_balanced";

    /// <summary>Per company, the sum of all lines is zero in functional and reporting currency: the trial balance balances.</summary>
    public const string TrialBalanceZero = "trial_balance_zero";

    /// <summary>The derived balance rows equal what the lines say, row by row and column by column.</summary>
    public const string BalancesMatchLines = "balances_match_lines";

    /// <summary>The tenant's audit chain recomputes link by link to its stored head.</summary>
    public const string AuditChainIntact = "audit_chain_intact";

    /// <summary>Every tenant table carries tenant_id with row-level security enabled, forced and policed.</summary>
    public const string TenantIsolation = "tenant_isolation";

    /// <summary>Gapless numbering series have no missing numbers and their counters sit right after the last issued one.</summary>
    public const string GaplessNumbering = "gapless_numbering";

    /// <summary>Every stock balance row equals the sum of its ledger entries and its reservations, in both directions (roadmap 3.2).</summary>
    public const string StockBalancesMatchLedger = "stock_balances_match_ledger";

    /// <summary>Per company and per item, the stock value entries sum to the inventory control accounts' journal lines (roadmap 3.3, hard scenario 15).</summary>
    public const string InventoryMatchesGl = "inventory_matches_gl";

    /// <summary>Per company and per goods receipt, the GRNI control account's lines net to the receipt's uninvoiced, unreturned expected cost (roadmap 4.3: GRNI equals uninvoiced receipts).</summary>
    public const string GrniMatchesReceipts = "grni_matches_receipts";

    /// <summary>Per company, the payables control accounts' lines net to the open items' remaining functional amounts, and the supplier-advances control to the advances (roadmap 4.7, DOMAIN_MODEL §13.1).</summary>
    public const string PayablesMatchOpenItems = "payables_match_open_items";

    /// <summary>Per bank account, the control account's lines referencing the bank subledger net to the bank transactions' functional amounts (roadmap 4.7 / 6.1).</summary>
    public const string BankMatchesTransactions = "bank_matches_transactions";

    public static readonly IReadOnlyList<string> All = [EntriesBalanced, TrialBalanceZero, BalancesMatchLines, AuditChainIntact, TenantIsolation, GaplessNumbering, StockBalancesMatchLedger, InventoryMatchesGl, GrniMatchesReceipts, PayablesMatchOpenItems, BankMatchesTransactions];
}

/// <summary>One check: what it looked at, whether it holds, and the first problems it found (never more than a page).</summary>
public sealed record InvariantResult(string Code, bool Passed, long Checked, IReadOnlyList<string> Problems, string Summary);

/// <summary>A run of the harness for one tenant (optionally one company): every check with its outcome.</summary>
public sealed record InvariantReport(Guid TenantId, Guid? CompanyId, DateTimeOffset RanAt, IReadOnlyList<InvariantResult> Checks, bool Passed)
{
    public IReadOnlyList<string> FailedCodes => Checks.Where(static c => !c.Passed).Select(static c => c.Code).ToList();
}

/// <summary>Runs the invariants for the current tenant inside the current unit of work; reads only.</summary>
public interface IInvariantHarness
{
    Task<InvariantReport> RunAsync(Guid? companyId = null, CancellationToken cancellationToken = default);
}
