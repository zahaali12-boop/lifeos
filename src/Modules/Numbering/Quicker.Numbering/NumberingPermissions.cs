using Quicker.Identity.Contracts;

namespace Quicker.Numbering;

public static class NumberingPermissions
{
    public const string SeriesRead = "numbering.series.read";
    public const string SeriesManage = "numbering.series.manage";
    public const string Reset = "numbering.counter.reset";
    public const string Allocate = "numbering.number.allocate";

    public static readonly PermissionDefinition[] All =
    [
        new(SeriesRead, "numbering", "Read numbering series, counters, allocations and the gapless audit"),
        new(SeriesManage, "numbering", "Create and edit numbering series and advance counters"),
        new(Reset, "numbering", "Move a counter backwards (reason required, audited)", IsSensitive: true),
        new(Allocate, "numbering", "Allocate a number through the API for an external document"),
    ];
}
