using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>One weapon's application against the target it is shooting now: the shots counted (misses included),
/// the percentage when one can be given, and the verdict.</summary>
public sealed record WeaponApplication(
    string Weapon,
    WeaponClass Class,
    string Target,
    int Shots,
    double? Percent,
    ApplicationVerdict Verdict,
    long Damage);
