using Quicker.Identity.Contracts;

namespace Quicker.Reporting;

/// <summary>Permission keys of reporting: running queries over the semantic models (each model also needs the read permission of the module it reads), managing saved reports, and refreshing the projected facts.</summary>
public static class ReportingPermissions
{
    public const string ReportRun = "reporting.report.run";
    public const string ReportManage = "reporting.report.manage";
    public const string ProjectionManage = "reporting.projection.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(ReportRun, "reporting", "Run queries over the semantic models, drill to their lines and see the query; each model also needs the read permission of the module it reads"),
        new(ReportManage, "reporting", "Save, share and schedule reports and dashboards"),
        new(ProjectionManage, "reporting", "Refresh or rebuild the projected facts of the reporting schema"),
    ];
}
