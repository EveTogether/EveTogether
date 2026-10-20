using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.Runs;

/// <summary>Where the loot table reads its figures from (ET-519): the pilot's own runs, or what the pilots sharing with a
/// server handed it.</summary>
public interface ILootTableSource
{
    Task<LootTableReading> ReadAsync(LootTableRequest request, CancellationToken cancellationToken = default);

    /// <summary>The activities the pilot has flown, most recent first: what the window offers when it is opened without a site.</summary>
    Task<IReadOnlyList<LootSiteChoice>> ListFlownAsync(CancellationToken cancellationToken = default);
}

/// <param name="Basis">What the pilot asked for. All pilots falls back to the own runs, with the reason, when no server can answer.</param>
/// <param name="CharacterIds">The pilot's own characters: whichever of them is connected to a server asks it.</param>
/// <param name="OnlyServer">A server to read, ignoring the pick; null for the pick, else the one with the most data.</param>
public sealed record LootTableRequest(ActivityStatsKey Key, EarningsBasis Basis, LootTableWindow Window,
    IReadOnlyList<int> CharacterIds, string? OnlyServer = null);

/// <param name="Basis">Whose runs the table is from, which is not always what was asked for.</param>
/// <param name="Reason">Why it is the own runs where the pilot asked for all pilots.</param>
public sealed record LootTableReading(LootTableDto Table, EarningsBasis Basis, string? ServerName, OwnRunsReason Reason,
    DateTime ReadAtUtc);

public sealed record LootSiteChoice(ActivityStatsKey Key, string? SiteName, int RunCount, DateTime LastFlownUtc);
