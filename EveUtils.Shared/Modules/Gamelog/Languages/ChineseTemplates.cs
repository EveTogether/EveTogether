namespace EveUtils.Shared.Modules.Gamelog.Languages;

internal static class ChineseTemplates
{
    public static GamelogTemplates Table { get; } = new()
    {
        Listener = "收听者",
        SessionStarted = "进程开始",
        Grazes = "轻轻擦过",
        GlancesOff = "擦过",
        Hits = "命中",
        Penetrates = "穿透",
        Smashes = "强力一击",
        Wrecks = "致命一击",
        DirectionTo = "对",
        DirectionFrom = "来自",
        MissOutSingle = "你的{weapon}完全没有打中{target}",
        MissOutGroup = "你的一组{weapon}完全没有打中{target}",
        MissInOwned = "{owner}的{weapon}完全没有击中你",
        MissInSource = "{source}完全没有打中你",
        ArmorRepairTo = "{amount}远程装甲维修量至{rest}",
        ArmorRepairBy = "{amount}远程装甲维修量由{rest}",
        ShieldBoostTo = "{amount}远程护盾回充增量至{rest}",
        ShieldBoostBy = "{amount}远程护盾回充增量由{rest}",
        HullRepairTo = "{amount}远程结构维修量至{rest}",
        HullRepairBy = "{amount}远程结构维修量由{rest}",
        CapacitorTransmittedBy = "{amount}远程电容传输量由{rest}",
        CapacitorTransmittedTo = "{amount}远程电容传输至{rest}",
        EnergyNeutralizedOut = "{amount} GJ能量中和{rest}",
        EnergyNeutralizedIn = "{amount} GJ能量中和{rest}",
        Mined = "你挖掘到{amount}单位的{ore}",
        CriticalMined = "出现采矿暴击！你额外挖掘到{amount}单位的{ore}",
        MiningResidue = "额外{amount}单位从小行星耗竭，成为残渣",
        Bounty = "{isk}添加到下一次赏金支付",
        Jumping = "从{gate}跳到{system}",
        Undocking = "正在从 {station} 离站进入 {system} 恒星系。",
        ResourceDepleted = "由于开采资源已尽，{module}已停转。",
        MiningBoost = "你的{module}已为{count}名舰队{成员|成员}提供加成。",
    };
}
