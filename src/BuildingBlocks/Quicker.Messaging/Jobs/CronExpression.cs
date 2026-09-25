using System.Globalization;
using Quicker.Kernel.Results;

namespace Quicker.Messaging.Jobs;

/// <summary>
/// Five-field cron (minute hour day-of-month month day-of-week) with <c>*</c>, lists, ranges and steps, evaluated
/// in a time zone. Day-of-month and day-of-week combine with OR when both are restricted, as in Vixie cron.
/// </summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronExpression(string text, bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek, bool domRestricted, bool dowRestricted)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = domRestricted;
        _dayOfWeekRestricted = dowRestricted;
    }

    public string Text { get; }

    public static Result<CronExpression> Parse(string? text)
    {
        var fields = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            return Error.Validation("schedule.cron_invalid", "A cron expression has five fields: minute hour day-of-month month day-of-week.");
        }

        var minutes = Field(fields[0], 0, 59);
        var hours = Field(fields[1], 0, 23);
        var daysOfMonth = Field(fields[2], 1, 31);
        var months = Field(fields[3], 1, 12);
        var daysOfWeek = Field(fields[4], 0, 7);
        if (minutes.IsFailure || hours.IsFailure || daysOfMonth.IsFailure || months.IsFailure || daysOfWeek.IsFailure)
        {
            return minutes.Error ?? hours.Error ?? daysOfMonth.Error ?? months.Error ?? daysOfWeek.Error!;
        }

        // 7 is Sunday too.
        var dow = daysOfWeek.Value;
        if (dow[7])
        {
            dow[0] = true;
        }

        return new CronExpression(string.Join(' ', fields), minutes.Value, hours.Value, daysOfMonth.Value, months.Value, dow, fields[2] != "*", fields[4] != "*");
    }

    /// <summary>The first occurrence strictly after <paramref name="after"/>, or null when none falls within the next five years.</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTime(after, timeZone);
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = candidate.AddYears(5);
        while (candidate < limit)
        {
            if (!_months[candidate.Month])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!DayMatches(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (timeZone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            var offset = timeZone.GetUtcOffset(candidate);
            return new DateTimeOffset(candidate, offset).ToUniversalTime();
        }

        return null;
    }

    private bool DayMatches(DateTime date)
    {
        var dom = _daysOfMonth[date.Day];
        var dow = _daysOfWeek[(int)date.DayOfWeek];
        return (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
        {
            (true, true) => dom || dow,
            (true, false) => dom,
            (false, true) => dow,
            _ => true,
        };
    }

    private static Result<bool[]> Field(string field, int min, int max)
    {
        var set = new bool[max + 1];
        foreach (var part in field.Split(','))
        {
            var stepSplit = part.Split('/');
            if (stepSplit.Length > 2)
            {
                return Invalid(field);
            }

            var step = 1;
            if (stepSplit.Length == 2 && (!int.TryParse(stepSplit[1], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1))
            {
                return Invalid(field);
            }

            int from;
            int to;
            var range = stepSplit[0];
            if (range == "*")
            {
                from = min;
                to = max;
            }
            else if (range.Contains('-', StringComparison.Ordinal))
            {
                var bounds = range.Split('-');
                if (bounds.Length != 2 || !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out from) || !int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out to) || from > to)
                {
                    return Invalid(field);
                }
            }
            else
            {
                if (!int.TryParse(range, NumberStyles.None, CultureInfo.InvariantCulture, out from))
                {
                    return Invalid(field);
                }

                to = stepSplit.Length == 2 ? max : from;
            }

            if (from < min || to > max)
            {
                return Invalid(field);
            }

            for (var value = from; value <= to; value += step)
            {
                set[value] = true;
            }
        }

        return set;
    }

    private static Error Invalid(string field) => Error.Validation("schedule.cron_invalid", $"Cron field '{field}' is not valid.").WithWhy(("field", field));
}
