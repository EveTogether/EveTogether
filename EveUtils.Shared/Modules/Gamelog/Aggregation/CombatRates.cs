namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// A character's current live combat rates for the multi-line graph: outgoing/incoming DPS plus the cap-warfare
/// activity rates (neutralized + capacitor transmitted, GJ/s). Sampled against wall-clock "now" so each decays to
/// zero when the activity stops. The fleet path sends these as separate <c>MetricKind</c> samples; the local meter
/// reads them all at once via the gamelog sampler.
/// </summary>
public readonly record struct CombatRates(double Dealt, double Received, double Neut, double Cap);
