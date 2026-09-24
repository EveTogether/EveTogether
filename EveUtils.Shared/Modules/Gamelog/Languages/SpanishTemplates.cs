namespace EveUtils.Shared.Modules.Gamelog.Languages;

internal static class SpanishTemplates
{
    public static GamelogTemplates Table { get; } = new()
    {
        Listener = "Oyente",
        SessionStarted = "Sesión iniciada",
        Grazes = "Roza",
        GlancesOff = "Alcanza",
        Hits = "Impacta",
        Penetrates = "Perfora",
        Smashes = "Destroza",
        Wrecks = "Destruye",
        DirectionTo = "a",
        DirectionFrom = "de",
        MissOutSingle = "Tu {weapon} no acierta en {target} por mucho.",
        MissOutGroup = "Tu grupo de {weapon} no acierta en {target} por mucho.",
        MissInOwned = "{weapon}, de {owner}, falla por mucho.",
        MissInSource = "{source} falla por mucho.",
        ArmorRepairTo = "{amount} blindaje remoto reparado para {rest}",
        ArmorRepairBy = "{amount} blindaje remoto reparado por {rest}",
        ShieldBoostTo = "{amount} de escudo remoto potenciado de {rest}",
        ShieldBoostBy = "{amount} de escudo remoto potenciado por {rest}",
        HullRepairTo = "{amount} del casco remoto reparado de {rest}",
        HullRepairBy = "{amount} casco remoto reparado por {rest}",
        CapacitorTransmittedBy = "{amount} condensador remoto transmitido por {rest}.",
        CapacitorTransmittedTo = "{amount} condensadores remotos transmitidos a {rest}.",
        EnergyNeutralizedOut = "{amount} GJ: energía neutralizada {rest}",
        EnergyNeutralizedIn = "{amount} GJ: energía neutralizada {rest}",
        Mined = "Has extraído {amount} unidades de {ore}",
        CriticalMined = "¡Extracción crítica completada! Has extraído {amount} unidades adicionales de {ore}",
        MiningResidue = "{amount} unidades adicionales del asteroide desperdiciadas como residuo",
        Bounty = "Se ha añadido {isk} a la siguiente recompensa.",
        Jumping = "Saltando de {gate} a {system}",
        Undocking = "Desacoplando nave de {station} en dirección al sistema solar {system}.",
        ResourceDepleted = "El módulo {module} se desactiva porque detecta que el recurso que estaba recolectando es una pálida sombra de lo que solía ser.",
        MiningBoost = "Tu {module} ha aplicado bonificaciones a {count} {miembro|miembros} de la flota.",
    };
}
