namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// A character's current live combat rates: DPS dealt and received (hp/s), energy neutralized and remote capacitor
/// each split by direction (GJ/s), and remote reps received and given (hp/s). Sampled against wall-clock "now" so each
/// decays to zero when the activity stops. Received and given stay apart because they are different situations —
/// being neuted is not neuting (ET-277). The fleet path sends these as separate <c>MetricKind</c> samples; the local
/// meter reads them all at once via the gamelog sampler.
/// </summary>
public readonly record struct CombatRates(
    double Dealt,
    double Received,
    double NeutIn,
    double NeutOut,
    double CapIn,
    double CapOut,
    double RepIn,
    double RepOut)
{
    /// <summary>Both directions of neut together, as clients from before ET-277 draw it.</summary>
    public double Neut => NeutIn + NeutOut;

    /// <summary>Both directions of remote capacitor together, as clients from before ET-277 draw it.</summary>
    public double Cap => CapIn + CapOut;
}
