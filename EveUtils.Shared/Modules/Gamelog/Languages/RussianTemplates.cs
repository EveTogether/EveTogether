namespace EveUtils.Shared.Modules.Gamelog.Languages;

internal static class RussianTemplates
{
    public static GamelogTemplates Table { get; } = new()
    {
        Listener = "Слушатель",
        SessionStarted = "Сеанс начат",
        Grazes = "Царапнул",
        GlancesOff = "Скользнул",
        Hits = "Попал",
        Penetrates = "Пробил",
        Smashes = "Раздробил",
        Wrecks = "Сокрушил",
        DirectionTo = "на",
        DirectionFrom = "из",
        MissOutSingle = "Ваше орудие {weapon} промахнулось мимо {target}",
        MissOutGroup = "Ваша группа {weapon} промахнулась мимо {target}",
        MissInOwned = "Орудие {weapon} пилота {owner} промахнулось мимо вашего корабля",
        MissInSource = "{source}: промах мимо вашего корабля",
        ArmorRepairTo = "{amount} единиц запаса прочности брони отремонтировано {rest}",
        ArmorRepairBy = "{amount} единиц запаса прочности брони получено дистанционным ремонтом от {rest}",
        ShieldBoostTo = "{amount} единиц запаса прочности щитов накачано {rest}",
        ShieldBoostBy = "{amount} единиц запаса прочности щитов получено накачкой от {rest}",
        HullRepairTo = "{amount} единиц запаса прочности корпуса отремонтировано {rest}",
        HullRepairBy = "{amount} единиц запаса прочности корпуса получено дистанционным ремонтом от {rest}",
        CapacitorTransmittedBy = "{amount} единиц запаса энергии накопителя получено от {rest}",
        CapacitorTransmittedTo = "{amount} единиц запаса энергии накопителя отправлено в {rest}",
        EnergyNeutralizedOut = "{amount} ГДж энергии нейтрализовано {rest}",
        EnergyNeutralizedIn = "{amount} ГДж энергии нейтрализовано {rest}",
        Mined = "Вы добыли {amount} ед. ресурса {ore}",
        CriticalMined = "Успешный крит. удар! Вы добыли ещё {amount} ед. ресурса {ore}",
        MiningResidue = "Ещё {amount} ед. превратилось в отходы",
        Bounty = "{isk} добавлено к следующей выплате вознаграждения",
        Jumping = "Осуществляется прыжок из {gate} в {system}",
        Undocking = "Выход из дока {station} в звездную систему {system}.",
        ResourceDepleted = "{module} деактивируется, так как добываемый им ресурс обращен в пыль.",
        MiningBoost = "Преимущества от вашего модуля {module} выданы {count} {пилоту|пилотам|пилотам} флота.",
    };
}
