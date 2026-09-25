using Quicker.Identity.Contracts;

namespace Quicker.Accounting;

public static class AccountingPermissions
{
    public const string ChartRead = "accounting.chart.read";
    public const string ChartManage = "accounting.chart.manage";
    public const string ProfileRead = "accounting.profile.read";
    public const string ProfileManage = "accounting.profile.manage";
    public const string JournalRead = "accounting.journal.read";
    public const string JournalPost = "accounting.journal.post";
    public const string JournalReverse = "accounting.journal.reverse";
    public const string JournalManage = "accounting.journal.manage";
    public const string JournalApprove = "accounting.journal.approve";
    public const string RoutinesRun = "accounting.routine.run";
    public const string PostInSoftClosed = "accounting.period.post_in_soft_closed";
    public const string BalanceRebuild = "accounting.balance.rebuild";

    public static readonly PermissionDefinition[] All =
    [
        new(ChartRead, "accounting", "Read charts of accounts, accounts, dimension rules, categories and statutory mappings"),
        new(ChartManage, "accounting", "Create and edit charts of accounts, accounts, dimension rules, categories and statutory mappings; assign a chart to a company"),
        new(ProfileRead, "accounting", "Read posting groups and posting profiles"),
        new(ProfileManage, "accounting", "Create and edit posting groups and posting profiles and activate a profile"),
        new(JournalRead, "accounting", "Read journal entries, lines and balances"),
        new(JournalPost, "accounting", "Post journal entries through the engine"),
        new(JournalReverse, "accounting", "Reverse a posted journal entry (reason required, audited)", IsSensitive: true),
        new(JournalManage, "accounting", "Create, edit, submit, cancel and import manual journals, recurring templates and deferral schedules"),
        new(JournalApprove, "accounting", "Approve or reject submitted journals", IsSensitive: true),
        new(RoutinesRun, "accounting", "Run the daily accounting routines now (auto-reversals, recurring journals, deferrals)", IsSensitive: true),
        new(PostInSoftClosed, "accounting", "Post into a soft-closed period"),
        new(BalanceRebuild, "accounting", "Rebuild the derived balances from the journal lines", IsSensitive: true),
    ];
}
