namespace EveUtils.Shared.Modules.Gamelog.Languages;

internal static class EnglishTemplates
{
    public static GamelogTemplates Table { get; } = new()
    {
        Listener = "Listener",
        SessionStarted = "Session Started",
        Grazes = "Grazes",
        GlancesOff = "Glances Off",
        Hits = "Hits",
        Penetrates = "Penetrates",
        Smashes = "Smashes",
        Wrecks = "Wrecks",
        DirectionTo = "to",
        DirectionFrom = "from",
        MissOutSingle = "Your {weapon} misses {target} completely",
        MissOutGroup = "Your group of {weapon} misses {target} completely",
        MissInOwned = "{weapon} belonging to {owner} misses you completely",
        MissInSource = "{source} misses you completely",
        ArmorRepairTo = "{amount} remote armor repaired to {rest}",
        ArmorRepairBy = "{amount} remote armor repaired by {rest}",
        ShieldBoostTo = "{amount} remote shield boosted to {rest}",
        ShieldBoostBy = "{amount} remote shield boosted by {rest}",
        HullRepairTo = "{amount} remote hull repaired to {rest}",
        HullRepairBy = "{amount} remote hull repaired by {rest}",
        CapacitorTransmittedBy = "{amount} remote capacitor transmitted by {rest}",
        CapacitorTransmittedTo = "{amount} remote capacitor transmitted to {rest}",
        EnergyNeutralizedOut = "{amount} GJ energy neutralized {rest}",
        EnergyNeutralizedIn = "{amount} GJ energy neutralized {rest}",
        Mined = "You mined {amount} units of {ore}",
        CriticalMined = "Critical mining success! You mined an additional {amount} units of {ore}",
        MiningResidue = "Additional {amount} units depleted from asteroid as residue",
        Bounty = "{isk} added to next bounty payout",
        Jumping = "Jumping from {gate} to {system}",
        Undocking = "Undocking from {station} to {system} solar system.",
        ResourceDepleted = "{module} deactivates as it finds the resource it was harvesting a pale shadow of its former glory.",
        MiningBoost = "Your {module} has applied bonuses to {count} fleet {member|members}.",
    };
}
