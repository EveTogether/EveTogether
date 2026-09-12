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

    /// <summary>Priced mining, valued through the price source — Mutanite at its fixed NPC price, everything else at
    /// the market's (ET-229). Null when there is mining but nothing on it can be priced yet.</summary>
    public required decimal? MiningIskValue { get; init; }

    /// <summary>Whether there is mining at all, priced or not — what tells "not priced yet" from "nothing mined".</summary>
    public required bool HasMining { get; init; }

    public required IReadOnlyList<RunIskParameter> Parameters { get; init; }

    /// <summary>When the run stopped — the moment a mission's bonus is judged at. Null while it is still going.</summary>
    public required DateTime? StoppedAtUtc { get; init; }

    /// <summary>What the fixed table owes this run's own character right now — the homefront payout table read
    /// against this character's own tick, N and outcome (ET-231). Null on every non-homefront run, and on a homefront
    /// run nothing is owed on yet (not ticked in, outcome not completed, N not known, or no table for the date).
    /// Never what was confirmed — that already counts through <see cref="Parameters"/>' own
    /// <c>RunParameterKey.FixedPayout</c> row, and <c>HomefrontPayoutIskContributor</c> is the one place the two are
    /// told apart.</summary>
    public required decimal? HomefrontExpectedPayoutIsk { get; init; }
}
