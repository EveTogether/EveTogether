namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// A doctrine skill minimum on a <see cref="FleetCompositionEntry"/>: the fit is only "at the minimum" once the
/// pilot has <see cref="SkillTypeId"/> at <see cref="Level"/> or higher, on top of what the fit itself requires.
/// Owned by its entry (own table, keyed on entry + skill), so it loads and cascades with it. Composition data only;
/// a pilot's trained skills never travel (D-179).
/// </summary>
public sealed class FleetCompositionEntrySkillMinimum
{
    public int SkillTypeId { get; set; }

    /// <summary>1..5.</summary>
    public int Level { get; set; }
}
