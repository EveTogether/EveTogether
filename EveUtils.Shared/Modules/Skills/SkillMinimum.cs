namespace EveUtils.Shared.Modules.Skills;

/// <summary>An explicit skill-level requirement, folded into a prerequisite-closure walk alongside the seed types'
/// own required-skill attributes — e.g. "this candidate skill itself, at the level being priced".</summary>
public sealed record SkillMinimum(int SkillTypeId, int Level);
