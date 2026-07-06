namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// The pilot-reported skill verdict for a member's assigned fit. Trained skills only
/// live on the pilot's own client, so that client evaluates the assigned
/// fit and reports just this verdict; viewers without local skill data show the can-fly / warning badge from it. An enum rather
/// than a nullable bool: <see cref="Unknown"/> means not (yet) evaluated — the fit changed since the last
/// report, or the pilot's client has not seen the assignment — which must stay distinct from "can't fly".
/// </summary>
public enum FitSkillVerdict
{
    Unknown = 0,
    CanFly = 1,
    MissingSkills = 2
}
