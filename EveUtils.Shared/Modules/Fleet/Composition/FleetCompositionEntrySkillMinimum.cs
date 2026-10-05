namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// A doctrine skill minimum on a <see cref="FleetCompositionEntry"/>: the fit is "at the minimum" once the pilot has <see cref="SkillTypeId"/> at <see cref="Level"/> or higher.
/// Owned by its entry and composition data only; a pilot's trained skills never travel (D-179).
/// </summary>
public sealed class FleetCompositionEntrySkillMinimum
{
    public int SkillTypeId { get; set; }

    /// <summary>1..5.</summary>
    public int Level { get; set; }
}
