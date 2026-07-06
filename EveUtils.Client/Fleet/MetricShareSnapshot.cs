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
/// </summary>
public sealed class MetricShareSnapshot(IReadOnlyDictionary<string, string> values)
{
    /// <summary>Personal metrics that are opt-IN (off until explicitly enabled): location (privacy) and bounty (ISK).</summary>
    public static bool IsOptIn(MetricKind kind) => kind is MetricKind.Location or MetricKind.Bounty;

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

    /// <summary>The effective decision for a character in a specific fleet: a per-fleet override wins over the global
    /// default; absent an override the global default applies (a new fleet inherits your baseline).</summary>
    public bool IsShared(long fleetId, int characterId, MetricKind kind)
    {
        var value = values.GetValueOrDefault(OverrideKeyFor(fleetId, characterId, kind));
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsShared(kind); // no override → follow the global default
    }

    /// <summary>The current override choice for the per-fleet dialog: 0 = inherit (no override), 1 = share, 2 = don't share.</summary>
    public int OverrideChoiceIndex(long fleetId, int characterId, MetricKind kind)
    {
        var value = values.GetValueOrDefault(OverrideKeyFor(fleetId, characterId, kind));
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    /// <summary>The single share key for all live combat lines (DPS out/in, neut, cap, …): one "share my live
    /// combat data" toggle gates every combat metric instead of a per-line checkbox.</summary>
    public const string CombatShareKey = "fleet.share.combat";

    /// <summary>Whether a kind is a live combat line gated by the one combat-share toggle.</summary>
    public static bool IsCombat(MetricKind kind) =>
        kind is MetricKind.Dps or MetricKind.DpsIn or MetricKind.Neut or MetricKind.Cap;

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
}
