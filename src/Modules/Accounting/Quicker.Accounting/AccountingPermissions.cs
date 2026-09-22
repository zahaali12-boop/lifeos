using Quicker.Identity.Contracts;

namespace Quicker.Accounting;

public static class AccountingPermissions
{
    public const string ChartRead = "accounting.chart.read";
    public const string ChartManage = "accounting.chart.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(ChartRead, "accounting", "Read charts of accounts, accounts, dimension rules, categories and statutory mappings"),
        new(ChartManage, "accounting", "Create and edit charts of accounts, accounts, dimension rules, categories and statutory mappings; assign a chart to a company"),
    ];
}
