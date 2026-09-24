namespace EveUtils.Shared.Modules.Gamelog.Languages;

/// <summary>
/// The message templates of one client language, one property per gamelog line shape. Read once from CCP's own
/// localization data (the message IDs are noted per property) and stored here as plain strings — nothing at runtime
/// touches the game files. <see cref="GamelogGrammar"/> turns the templates into regexes.
///
/// Template grammar: <c>{amount}</c>, <c>{isk}</c>, <c>{ore}</c>, <c>{weapon}</c>, <c>{target}</c>, <c>{source}</c>,
/// <c>{owner}</c>, <c>{module}</c>, <c>{count}</c>, <c>{gate}</c>, <c>{station}</c>, <c>{system}</c> are the values a
/// line carries; <c>{rest}</c> is "&lt;counterparty&gt; - &lt;module&gt;"; <c>{a|b}</c> is a word the game inflects
/// (plural, gender). Everything else is literal text, whitespace being any run of spaces.
/// </summary>
internal sealed class GamelogTemplates
{
    // Header words — 59426, 59427.
    public required string Listener { get; init; }
    public required string SessionStarted { get; init; }

    // The word between the amount and the counterparty of a damage line. Not one template per direction like the
    // rest: the game writes "<amount> <word> <target> - <weapon> - <quality>" itself — 285197 (out), 285198 (in).
    public required string DirectionTo { get; init; }
    public required string DirectionFrom { get; init; }

    // Hit qualities — 285185 .. 285190.
    public required string Grazes { get; init; }
    public required string GlancesOff { get; init; }
    public required string Hits { get; init; }
    public required string Penetrates { get; init; }
    public required string Smashes { get; init; }
    public required string Wrecks { get; init; }

    // Misses — 285200 (your weapon), 285201 (your weapon group), 285202 (a weapon owned by a pilot), 285203 (an NPC).
    public required string MissOutSingle { get; init; }
    public required string MissOutGroup { get; init; }
    public required string MissInOwned { get; init; }
    public required string MissInSource { get; init; }

    // Remote assistance, a template per direction: to = 526468 / 526550 / 526553 / 526557, by = 526469 / 526551 /
    // 526554 / 526556.
    public required string ArmorRepairTo { get; init; }
    public required string ArmorRepairBy { get; init; }
    public required string ShieldBoostTo { get; init; }
    public required string ShieldBoostBy { get; init; }
    public required string HullRepairTo { get; init; }
    public required string HullRepairBy { get; init; }
    public required string CapacitorTransmittedTo { get; init; }
    public required string CapacitorTransmittedBy { get; init; }

    // Energy neutralizer — 517454 (you neut a target), 517492 (you are neuted).
    public required string EnergyNeutralizedOut { get; init; }
    public required string EnergyNeutralizedIn { get; init; }

    // Mining — 1019582, 1019592, 517487. Bounty — 517488.
    public required string Mined { get; init; }
    public required string CriticalMined { get; init; }
    public required string MiningResidue { get; init; }
    public required string Bounty { get; init; }

    // Location — 239314, 238851.
    public required string Jumping { get; init; }
    public required string Undocking { get; init; }

    // Notify lines — 259462, 518842.
    public required string ResourceDepleted { get; init; }
    public required string MiningBoost { get; init; }
}
