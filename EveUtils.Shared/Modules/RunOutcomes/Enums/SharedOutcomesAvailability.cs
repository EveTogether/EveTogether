namespace EveUtils.Shared.Modules.RunOutcomes.Enums;

/// <summary>Whether a server hands out an activity's shared outcomes in a window (Jithran 19-10): only once at least
/// <see cref="RunOutcomesModule.MinimumPilots"/> pilots contribute, so one pilot's runs are never readable through it.
/// Below that a client falls back to its own runs.</summary>
public enum SharedOutcomesAvailability
{
    Shared,
    TooFewPilots
}
