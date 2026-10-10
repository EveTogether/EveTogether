namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// One row of HIT QUALITY (ET-474): a target (or source) and the weapon on the other side, how many shots landed in
/// each hit word, and the damage they did. <see cref="Application"/> is empty for incoming rows.
/// </summary>
public sealed record HitQualityRowViewModel(
    string Target, string Weapon, bool HasWeapon, string Shots, string[] Counts,
    string Min, string Average, string Max, string Total,
    string Application, string? ApplicationTip, bool IsSweet, bool IsAdjust, bool IsUnjudged)
{
    public bool HasApplication => Application.Length > 0;

    public bool IsOk => HasApplication && !IsSweet && !IsAdjust && !IsUnjudged;

    /// <summary>Zebra striping, set by the caller once the row's final position in its block is known.</summary>
    public bool IsAlternate { get; set; }
}
