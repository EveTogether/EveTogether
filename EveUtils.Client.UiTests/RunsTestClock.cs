namespace EveUtils.Client.UiTests;

/// <summary>The "now" the runs screen is built on in tests: inside the month the seeded runs are dated in, so a test
/// does not go empty when the calendar moves on.</summary>
internal sealed class RunsTestClock : TimeProvider
{
    public static readonly RunsTestClock Fixed = new();

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
