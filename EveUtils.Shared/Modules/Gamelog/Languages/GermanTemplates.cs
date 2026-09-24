namespace EveUtils.Shared.Modules.Gamelog.Languages;

internal static class GermanTemplates
{
    public static GamelogTemplates Table { get; } = new()
    {
        Listener = "Empfänger",
        SessionStarted = "Sitzung gestartet",
        Grazes = "Leichter Streifschuss",
        GlancesOff = "Streifschuss ohne Schäden",
        Hits = "Treffer",
        Penetrates = "Einschlag",
        Smashes = "Schwerer Treffer",
        Wrecks = "Vernichtender Treffer",
        DirectionTo = "nach",
        DirectionFrom = "von",
        MissOutSingle = "Ihr {weapon} hat {target} völlig verfehlt",
        MissOutGroup = "Ihre {weapon}-Gruppe hat {target} völlig verfehlt",
        MissInOwned = "{weapon} von {owner} hat Sie völlig verfehlt",
        MissInSource = "{source} verfehlt Sie völlig",
        ArmorRepairTo = "{amount} Panzerungs-Fernreparatur zu {rest}",
        ArmorRepairBy = "{amount} Panzerungs-Fernreparatur von {rest}",
        ShieldBoostTo = "{amount} Schildfernbooster aktiviert zu {rest}",
        ShieldBoostBy = "{amount} Schildfernbooster aktiviert von {rest}",
        HullRepairTo = "{amount} Rumpf-Fernreparatur zu {rest}",
        HullRepairBy = "{amount} Rumpf-Fernreparatur von {rest}",
        CapacitorTransmittedBy = "{amount} Fernenergiespeicher übertragen von {rest}",
        CapacitorTransmittedTo = "{amount} Fernenergiespeicher übertragen zu {rest}",
        EnergyNeutralizedOut = "{amount} GJ Energie neutralisiert {rest}",
        EnergyNeutralizedIn = "{amount} GJ Energie neutralisiert {rest}",
        Mined = "Sie haben {amount} Einheiten {ore} abgebaut",
        CriticalMined = "Kritischer Bergbauerfolg! Sie haben zusätzliche {amount} Einheiten von {ore} abgebaut",
        MiningResidue = "Zusätzliche {amount} Einheiten aus Asteroid als Rückstände erschöpft",
        Bounty = "{isk} zur nächsten Kopfgeldzahlung hinzugefügt",
        Jumping = "Springe von {gate} nach {system}",
        Undocking = "Abdocken von {station} zum Sonnensystem {system}.",
        ResourceDepleted = "{module} schaltet ab, da die abgebaute Materie nur noch ein ausgesaugter Schatten dessen ist, was sie einmal war.",
        MiningBoost = "Ihr {module} gewährt {count} {Flottenmitglied|Flottenmitgliedern} einen Bonus.",
    };
}
