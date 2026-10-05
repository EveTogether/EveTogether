namespace EveUtils.Shared.Modules.Dogma;

/// <summary>SDE-derived NPC e-war ranges and defenses. A null range means the effect is absent;
/// <see cref="Ehp"/> uses <see cref="DamageProfile.Uniform"/> across shield, armor and hull.</summary>
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
