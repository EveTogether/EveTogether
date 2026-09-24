namespace EveUtils.Shared.Modules.Gamelog.Languages;

internal static class FrenchTemplates
{
    public static GamelogTemplates Table { get; } = new()
    {
        Listener = "Auditeur",
        SessionStarted = "Session commencée",
        Grazes = "Égratigne",
        GlancesOff = "Effleure",
        Hits = "Touche",
        Penetrates = "Pénètre",
        Smashes = "Frappe",
        Wrecks = "Détruit",
        DirectionTo = "à",
        DirectionFrom = "de",
        MissOutSingle = "Votre {weapon} a complètement manqué {target}",
        MissOutGroup = "Votre groupe de {weapon} a complètement manqué {target}",
        MissInOwned = "L'arme {weapon} appartenant à {owner} vous a complètement {manqué|manquée}.",
        MissInSource = "{source} vous a complètement manqué",
        ArmorRepairTo = "{amount} points de blindage transférés à distance à {rest}",
        ArmorRepairBy = "{amount} points de blindage réparés à distance par {rest}",
        ShieldBoostTo = "{amount} points de boucliers transférés à distance à {rest}",
        ShieldBoostBy = "{amount} points de boucliers transférés à distance par {rest}",
        HullRepairTo = "{amount} points de structure transférés à distance à {rest}",
        HullRepairBy = "{amount} points de structure réparés à distance par {rest}",
        CapacitorTransmittedBy = "{amount} points de capaciteur transférés à distance par {rest}",
        CapacitorTransmittedTo = "{amount} points de capaciteur transférés à distance à {rest}",
        EnergyNeutralizedOut = "{amount} GJ d'énergie neutralisée en faveur de {rest}",
        EnergyNeutralizedIn = "{amount} GJ d'énergie neutralisée aux dépens de {rest}",
        Mined = "Vous avez extrait {amount} unités de {ore}",
        CriticalMined = "Succès de coup critique d'extraction ! Vous avez extrait {amount} unités de {ore} supplémentaires",
        MiningResidue = "{amount} unités supplémentaires épuisées de l'astéroïde comme résidu",
        Bounty = "{isk} ajoutés à la prochaine prime.",
        Jumping = "Saute de {gate} à {system}",
        Undocking = "Part de {station} pour rejoindre le système solaire {system}.",
        ResourceDepleted = "{module} : désactivation, en effet la quantité de ressources qu'il collectait a fondu comme neige au soleil.",
        MiningBoost = "Votre {module} confère des bonus à {count} {membre|membres}.de flotte.",
    };
}
