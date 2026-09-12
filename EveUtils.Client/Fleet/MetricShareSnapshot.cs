using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>
/// An immutable view of the per-metric share settings at one moment. Defaults match EVE-style simplicity: every
/// metric is shared with the fleet by default (opt-OUT) except <see cref="MetricKind.Location"/>, which stays
/// private until explicitly enabled (opt-IN). A new <see cref="MetricKind"/> therefore inherits a
/// sensible default (shared) without any extra wiring.
///
/// A character may override the global default for a single fleet (e.g. "never share my location globally, but do in
/// this one op"). The effective decision is the per-(fleet, character, kind) override if set, else the global default.
///
/// On a shared fleet run (<paramref name="sharedRuns"/>, ET-242) loot and bounty go one step further: the run's own
/// choice first, then the fleet's override, and without either of them shared — the run window says so on its face and
/// takes one click to turn off. The global opt-in is never consulted there, and never changed anywhere else.
/// </summary>
public sealed class MetricShareSnapshot(
    IReadOnlyDictionary<string, string> values,
    IReadOnlyDictionary<(long FleetId, int CharacterId), string>? sharedRuns = null)
{
    /// <summary>Personal metrics that are opt-IN (off until explicitly enabled): location (privacy) and what a pilot
    /// made — bounty and loot. A new kind inherits "shared", so ISK has to be named here or it goes out by
    /// default, which is the opposite of how this client already treats the bounty figure beside it.</summary>
    public static bool IsOptIn(MetricKind kind) =>
        kind is MetricKind.Location or MetricKind.Bounty or MetricKind.Loot;

    /// <summary>What a pilot made on a run, and so what a shared run decides for itself (ET-242).</summary>
    public static bool IsRunScoped(MetricKind kind) => kind is MetricKind.Loot or MetricKind.Bounty;

    /// <summary>The global default for a metric kind (the baseline for all fleets/characters).</summary>
    public bool IsShared(MetricKind kind)
    {
        var value = values.GetValueOrDefault(KeyFor(kind));

        // Opt-IN metrics (location, bounty) are shared only when explicitly turned on.
        if (IsOptIn(kind))
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        // Every other metric is opt-OUT: shared unless the user explicitly turned it off.
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The effective decision for a character in a specific fleet: on a shared run the run's own choice for
    /// loot and bounty, then a per-fleet override, then — on that run — shared, elsewhere the global default (a new
    /// fleet inherits your baseline).</summary>
    public bool IsShared(long fleetId, int characterId, MetricKind kind)
    {
        bool? fleetChoice = _Choice(OverrideKeyFor(fleetId, characterId, kind));
        if (IsRunScoped(kind) && sharedRuns?.GetValueOrDefault((fleetId, characterId)) is { } groupCode)
            return _Choice(RunKeyFor(groupCode, kind)) ?? fleetChoice ?? true;

        return fleetChoice ?? IsShared(kind);
    }

    /// <summary>The current override choice for the per-fleet dialog: 0 = inherit (no override), 1 = share, 2 = don't share.</summary>
    public int OverrideChoiceIndex(long fleetId, int characterId, MetricKind kind) =>
        _Choice(OverrideKeyFor(fleetId, characterId, kind)) switch
        {
            true => 1,
            false => 2,
            null => 0
        };

    /// <summary>The single share key for all live combat lines (DPS out/in, neut, cap, …): one "share my live
    /// combat data" toggle gates every combat metric instead of a per-line checkbox.</summary>
    public const string CombatShareKey = "fleet.share.combat";

    /// <summary>Whether a kind is a live combat line gated by the one combat-share toggle. <see cref="MetricKind.NeutIn"/>
    /// belongs here for the same reason the rest do — and pointedly so: it is the received half of
    /// <see cref="MetricKind.Neut"/>, so leaving it out would push the very same fact past a toggle the user turned
    /// off, under a key of its own that defaults to shared. <see cref="MetricKind.RepIn"/> is live combat data in the
    /// same sense — reps landing on a member in a fight — even though (unlike neut) there is no combined rep kind
    /// beside it to have shared a key with by default.</summary>
    public static bool IsCombat(MetricKind kind) =>
        kind is MetricKind.Dps or MetricKind.DpsIn or MetricKind.Neut or MetricKind.Cap or MetricKind.NeutIn or MetricKind.RepIn;

    /// <summary>The global client-setting key for a metric kind. Every combat line shares one key; Location reuses its
    /// existing key for backward compatibility.</summary>
    public static string KeyFor(MetricKind kind) =>
        kind == MetricKind.Location ? LocationMetricSource.ShareLocationSettingKey
        : IsCombat(kind) ? CombatShareKey
        : $"fleet.share.{kind.ToString().ToLowerInvariant()}";

    /// <summary>The per-(fleet, character) override key for a metric kind. Absent = follow the global default. Combat
    /// lines collapse to one "combat" override so a per-fleet choice covers DPS + neut + cap together.</summary>
    public static string OverrideKeyFor(long fleetId, int characterId, MetricKind kind) =>
        $"fleet.{fleetId}.{characterId}.share." +
        (kind == MetricKind.Location ? "location" : IsCombat(kind) ? "combat" : kind.ToString().ToLowerInvariant());

    /// <summary>The per-run key for loot or bounty (ET-242), one for every own character on the run: the run window's
    /// toggle is the run's, not a character's. Absent = the fleet's override, then shared.</summary>
    public static string RunKeyFor(string groupCode, MetricKind kind) =>
        $"fleet.run.{groupCode}.share.{kind.ToString().ToLowerInvariant()}";

    private bool? _Choice(string key) => values.GetValueOrDefault(key) switch
    {
        { } value when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
        { } value when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
        _ => null
    };
}
