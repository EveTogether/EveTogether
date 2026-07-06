using System;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>The cost of closing one skill gap (fit-validation): the skill points still to train and the Omega
/// time it takes at the character's effective attribute rate (base allocation + attribute implants).</summary>
public sealed record SkillTrainingEstimate(double SkillPointsRequired, TimeSpan TrainingTime);
