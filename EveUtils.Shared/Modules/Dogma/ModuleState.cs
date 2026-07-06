namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The activation state of a fitted item, ordered from least to most active. An effect applies only when the item's
/// state reaches the effect's required state (mapped from its effectCategory in pass 2). Ships, characters, skills,
/// implants and rigs are effectively always-on.
/// </summary>
public enum ModuleState
{
    Passive = 0,
    Online = 1,
    Active = 2,
    Overload = 3
}
