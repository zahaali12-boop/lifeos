namespace Quicker.Kernel.Time;

/// <summary>The only source of "now". Injected everywhere so tests control time (ADR-0011).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Today's calendar date in the given IANA time zone (a company's or a user's).</summary>
    DateOnly TodayIn(string timeZoneId);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly TodayIn(string timeZoneId) => Clocks.ToDateIn(UtcNow, timeZoneId);
}

/// <summary>A clock tests can set and advance.</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    public DateOnly TodayIn(string timeZoneId) => Clocks.ToDateIn(_now, timeZoneId);

    public void Set(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

public static class Clocks
{
    public static DateOnly ToDateIn(DateTimeOffset instant, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return DateOnly.FromDateTime(local.DateTime);
    }
}
