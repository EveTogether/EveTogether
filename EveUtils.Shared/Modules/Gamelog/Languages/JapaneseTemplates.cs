namespace EveUtils.Shared.Modules.Gamelog.Languages;

internal static class JapaneseTemplates
{
    public static GamelogTemplates Table { get; } = new()
    {
        Listener = "傍聴者",
        SessionStarted = "セッション開始",
        Grazes = "擦過",
        GlancesOff = "軽微",
        Hits = "直撃",
        Penetrates = "小破",
        Smashes = "中破",
        Wrecks = "大破",
        DirectionTo = "から",
        DirectionFrom = "から",
        MissOutSingle = "あなたの{weapon}の攻撃は{target}を完全に外した",
        MissOutGroup = "あなたの{weapon}グループの攻撃は{target}を完全に外した",
        MissInOwned = "{owner}の{weapon}の攻撃はあなたを完全に外した",
        MissInSource = "{source}の攻撃はあなたを完全に外した",
        ArmorRepairTo = "{amount}リモートアーマーリペアを{rest}に与えました",
        ArmorRepairBy = "{amount}リモートアーマーリペアを{rest}から受けました",
        ShieldBoostTo = "{amount}リモートシールドブーストを{rest}に与えました",
        ShieldBoostBy = "{amount}リモートシールドブーストを{rest}で受けました",
        HullRepairTo = "{amount}リモート船体リペアを{rest}に与えました",
        HullRepairBy = "{amount}リモート船体リペアを{rest}で受けました",
        CapacitorTransmittedBy = "{amount}リモートキャパシタを{rest}で受けました",
        CapacitorTransmittedTo = "{amount}のリモートキャパシタが{rest}に転送されました",
        EnergyNeutralizedOut = "{amount}GJ エネルギーニュートラライズ 対象:{rest}",
        EnergyNeutralizedIn = "{amount}GJのエネルギーが解放されました{rest}",
        Mined = "あなたは{amount}ユニットの{ore}を採掘しました",
        CriticalMined = "クリティカル採掘に成功！ 追加で{amount}ユニットの{ore}を採掘しました",
        MiningResidue = "追加で{amount}ユニットが残留物として消費されました",
        Bounty = "{isk}を次回の懸賞金振り込みに追加",
        Jumping = "{gate}から{system}へジャンプ中",
        Undocking = "{station} から {system} へ出港",
        ResourceDepleted = "{module} を停止します。採掘していた豊富な資源はもうありません。",
        MiningBoost = "{module}のボーナスが{count}フリート{メンバー|メンバー}に適用されました。",
    };
}
