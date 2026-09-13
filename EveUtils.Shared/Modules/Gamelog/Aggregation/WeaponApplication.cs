using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>One weapon's application against the target it is shooting now: the shots counted (misses included),
/// the percentage when one can be given, the verdict, and — for a missile that lands short — why (ET-282).</summary>
public sealed record WeaponApplication(
    string Weapon,
    WeaponClass Class,
    string Target,
    int Shots,
    double? Percent,
    ApplicationVerdict Verdict,
    long Damage,
    string? Cause = null);
