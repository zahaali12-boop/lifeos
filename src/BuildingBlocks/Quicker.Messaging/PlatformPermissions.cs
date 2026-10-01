using Quicker.Identity.Contracts;

namespace Quicker.Messaging;

public static class PlatformPermissions
{
    public const string JobRead = "platform.job.read";
    public const string JobManage = "platform.job.manage";
    public const string ScheduleRead = "platform.schedule.read";
    public const string ScheduleManage = "platform.schedule.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(JobRead, "platform", "See the tenant's background jobs, their progress and results"),
        new(JobManage, "platform", "Retry or cancel the tenant's background jobs"),
        new(ScheduleRead, "platform", "See the tenant's schedules"),
        new(ScheduleManage, "platform", "Create, change and remove the tenant's schedules"),
    ];
}
