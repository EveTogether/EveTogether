namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// An NPC's ranged e-war and defensive stats, read straight off the SDE (ET-367). Each range is null when the type
/// does not carry that behavior effect at all — never 0, so a caller can tell "no scramble" from "scrambles at 0 m".
/// <see cref="Ehp"/> is the shield+armor+hull EHP under <see cref="DamageProfile.Uniform"/>, the same profile and
/// formula (<see cref="DamageProfile.WeightedEhp"/>) the fit-simulator uses for a player ship.
/// </summary>
public sealed record NpcEwarProfile(
    double? ScrambleRange,
    double? NeutralizerRange,
    double? WebifierRange,
    double? SensorDampenerRange,
    double? TrackingDisruptorRange,
    double? GuidanceDisruptorRange,
    double? TargetPainterRange,
    double? RemoteArmorRepairerRange,
    double? VortonRange,
    double Ehp,
    double SignatureRadius,
    double MaxVelocity);
