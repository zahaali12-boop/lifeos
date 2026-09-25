using Quicker.Identity.Contracts;

namespace Quicker.Tax;

/// <summary>Permission keys of tax; the accountant template grants <c>tax.*</c>.</summary>
public static class TaxPermissions
{
    public const string SetupRead = "tax.setup.read";
    public const string SetupManage = "tax.setup.manage";
    public const string ReturnRead = "tax.return.read";
    public const string ReturnFile = "tax.return.file";

    public static readonly PermissionDefinition[] All =
    [
        new(SetupRead, "tax", "Read tax regimes, codes, rates, groups, the determination matrix, registrations and exemptions"),
        new(SetupManage, "tax", "Install tax templates and change regimes, codes, rates, groups, determination rules, registrations and exemptions"),
        new(ReturnRead, "tax", "Read tax returns and the tax entries behind them"),
        new(ReturnFile, "tax", "File a tax return, which locks its period"),
    ];
}
