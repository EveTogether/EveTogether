namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// One run as far as its ISK goes: the raw facts every contributor reads, never a figure a screen already added up
/// (ET-256). A fact a new contributor needs becomes a new required member here, so every place that builds one — a
/// stored run (<see cref="RunIskFactsReader"/>) and the open run window — stops compiling until it says what it has.
/// </summary>
public sealed record RunIskFacts
{
    /// <summary>What the gamelog's bounty lines paid out.</summary>
    public required decimal BountyIsk { get; init; }

    /// <summary>Priced loot gained less priced loot lost; null when not one counted line has a known price.</summary>
    public required decimal? LootIskNet { get; init; }

    /// <summary>Whether there is loot at all, priced or not — what tells "not priced yet" from "nothing".</summary>
    public required bool HasLoot { get; init; }

    /// <summary>What CONSUMABLES charges the run — a positive cost, contributed as negative ISK (ET-249). Null when
    /// there is a confirmed count but no price for its filament type yet.</summary>
    public required decimal? ConsumableIskCost { get; init; }

    /// <summary>Whether a filament count was ever confirmed for this run, priced or not — what tells "not priced
    /// yet" from "nothing to charge".</summary>
    public required bool HasConsumables { get; init; }

    public required IReadOnlyList<RunIskParameter> Parameters { get; init; }

    /// <summary>When the run stopped — the moment a mission's bonus is judged at. Null while it is still going.</summary>
    public required DateTime? StoppedAtUtc { get; init; }
}
