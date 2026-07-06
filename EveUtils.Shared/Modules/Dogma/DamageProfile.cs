namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// An incoming damage profile (EM / Thermal / Kinetic / Explosive weights, Σ = 1.0). Used to compute weighted EHP
/// from a ship's resistance layers: EHP = HP / Σ(wᵢ · resonanceᵢ), where resonances are SDE attributes [EM, Th,
/// Kin, Exp] (shield [271,274,273,272] / armor [267,270,269,268] / structure [113,110,109,111]).
/// <see cref="Uniform"/> (0.25 each) is the default and produces results byte-identical to the old mean-resonance
/// formula, ensuring no regression in existing fits.
/// </summary>
public sealed record DamageProfile(double Em, double Th, double Kin, double Exp)
{
    /// <summary>Default uniform 25/25/25/25 profile — produces the same EHP as the old mean-resonance formula.</summary>
    public static readonly DamageProfile Uniform = new(0.25, 0.25, 0.25, 0.25);

    /// <summary>Name for display; null means it has no named preset (custom / individual NPC).</summary>
    public string? Name { get; init; }

    /// <summary>Returns a new profile with all four weights scaled so they sum to 1.0. If all weights are zero,
    /// returns <see cref="Uniform"/>.</summary>
    public DamageProfile Normalized()
    {
        var sum = Em + Th + Kin + Exp;
        if (sum <= 0)
            return Uniform;
        return new DamageProfile(Em / sum, Th / sum, Kin / sum, Exp / sum);
    }
}
