namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// Run totals per day over a date range — counted by the same code as <c>runs/summary</c> and the home, so the days of a
/// month add up to that month's summary. ISK is the own characters' share. Public, versioned DTO.
/// </summary>
/// <param name="TimeZone">The client's local time zone id; the days and <c>DayStart</c> are in it.</param>
/// <param name="UtcOffset">The zone's offset from UTC now, as +HH:mm.</param>
/// <param name="DayStart">Where a day begins, HH:mm.</param>
public sealed record RunsDaysDto(
    string TimeZone,
    string UtcOffset,
    string DayStart,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<RunsDayDto> Days);

/// <param name="Date">The day a run belongs to: the date of its start minus the day start, so with 06:00 a run at 01:30 is
/// the previous day's.</param>
/// <param name="StartsAtUtc">When the day begins.</param>
/// <param name="IskPerHour">Over the runs with a flown time only.</param>
/// <param name="Sources">What each source brought in or took, a ship loss as a negative amount; Isk is their sum.</param>
/// <param name="Kinds">Only with <c>breakdown=kind</c>: the kinds that had a run that day.</param>
public sealed record RunsDayDto(
    DateOnly Date,
    DateTime StartsAtUtc,
    int Runs,
    long FlownSeconds,
    decimal? Isk,
    decimal? IskPerHour,
    IReadOnlyList<RunsSourceDto> Sources,
    IReadOnlyList<RunsDayKindDto>? Kinds);

/// <param name="Source">bounty, loot, rewards, consumables, mining, homefrontpayout or shiploss.</param>
public sealed record RunsSourceDto(string Source, decimal Isk);

/// <param name="Kind">abyssal, combat, mission or mining.</param>
public sealed record RunsDayKindDto(
    string Kind,
    int Runs,
    long FlownSeconds,
    decimal? Isk,
    decimal? IskPerHour);
