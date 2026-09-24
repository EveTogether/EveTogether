using System.Globalization;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One ATTACKERS row on the killmail detail screen (ET-333): who, their ship/weapon, damage and share,
/// with the FINAL BLOW, TOP DAMAGE and YOU badges — the same three the mockup marks on a row of its own.</summary>
public sealed class KillmailDetailAttackerRowViewModel(
    string name, string subText, string shipText, string? weaponText, int damageDone, double sharePercent,
    bool isFinalBlow, bool isTopDamage, bool isYou, bool isNpc)
{
    public string Name { get; } = name;

    public string SubText { get; } = subText;

    public string ShipText { get; } = shipText;

    public string? WeaponText { get; } = weaponText;

    public string DamageText { get; } = damageDone.ToString("N0", CultureInfo.InvariantCulture);

    public string PercentText { get; } = sharePercent.ToString("0", CultureInfo.InvariantCulture) + "%";

    public bool IsFinalBlow { get; } = isFinalBlow;

    public bool IsTopDamage { get; } = isTopDamage;

    public bool IsYou { get; } = isYou;

    public bool IsNpc { get; } = isNpc;
}
