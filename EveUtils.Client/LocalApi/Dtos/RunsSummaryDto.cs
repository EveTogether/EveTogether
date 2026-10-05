namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// Run totals over one period and kind — the same figures the home's earnings tiles and the runs overview show, counted
/// by the same code. ISK is the own characters' share. ISK figures are null where nothing in the period was valued,
/// never a zero that would claim it was. Public, versioned DTO.
/// </summary>
/// <param name="Period">session, today, week or month.</param>
/// <param name="Kind">all, abyssal, combat, mission or mining.</param>
/// <param name="FromUtc">Where the period starts; it runs until now.</param>
/// <param name="IskPerHour">Over the runs with a flown time only.</param>
/// <param name="BestDrop">Only for kind all: loot is not counted per kind.</param>
/// <param name="Abyssal">Per tier and weather; only for kind abyssal.</param>
public sealed record RunsSummaryDto(
    string Period,
    string Kind,
    DateTime FromUtc,
    int Runs,
    long FlownSeconds,
    decimal? Isk,
    decimal? IskPerHour,
    decimal? AverageIskPerRun,
    long? AverageRunSeconds,
    int ShipsLost,
    RunsBestDropDto? BestDrop,
    IReadOnlyList<RunsCharacterTotalDto> Characters,
    IReadOnlyList<AbyssalBreakdownDto>? Abyssal);
