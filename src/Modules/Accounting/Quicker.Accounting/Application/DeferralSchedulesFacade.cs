using Quicker.Accounting.Contracts;
using Quicker.Kernel.Results;

namespace Quicker.Accounting.Application;

/// <summary>The deferral engine behind <see cref="IDeferralSchedules"/>: documents of other modules create and cancel schedules by account code.</summary>
internal sealed class DeferralSchedulesFacade(DeferralService deferrals) : IDeferralSchedules
{
    public async Task<Result<DeferralScheduleState>> CreateAsync(NewDeferralSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var created = await deferrals.CreateAsync(schedule.CompanyId, new SaveDeferralRequest(schedule.Kind, schedule.BalanceAccountCode, schedule.TargetAccountCode, schedule.StartsOn, schedule.Periods, schedule.TotalAmount, schedule.Currency,
            Dimensions: schedule.Dimensions, Description: schedule.Description.Values, SourceDocumentType: schedule.SourceDocumentType, SourceLineId: schedule.SourceLineId), cancellationToken);
        return created.IsFailure ? created.Error! : State(created.Value);
    }

    public async Task<DeferralScheduleState?> FindAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedule = await deferrals.GetAsync(scheduleId, cancellationToken);
        return schedule is null ? null : State(schedule);
    }

    public async Task<Result<DeferralScheduleState>> CancelAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var cancelled = await deferrals.CancelAsync(scheduleId, cancellationToken);
        return cancelled.IsFailure ? cancelled.Error! : State(cancelled.Value);
    }

    private static DeferralScheduleState State(DeferralScheduleSummary s) => new(s.Id, s.Status, s.Periods, s.TotalAmount, s.PostedAmount, s.RemainingAmount);
}
