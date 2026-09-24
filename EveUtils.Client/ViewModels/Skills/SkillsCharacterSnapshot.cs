using System;
using System.Collections.Generic;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// Everything CATALOGUE and TRAINING QUEUE (ET-16) need for one character, read once off the UI thread when the
/// character changes and shared by both tabs — avoids threading four separate reads through both view-models.
/// </summary>
public sealed record SkillsCharacterSnapshot(
    ISdeAccessor Sde,
    IReadOnlyDictionary<int, int> Levels,
    IReadOnlyList<CharacterSkillQueueEntry> Queue,
    CharacterAttributes? Attributes,
    DateTimeOffset Now)
{
    public int LevelOf(int skillTypeId) => Levels.TryGetValue(skillTypeId, out var level) ? level : 0;

    /// <summary>SP/min for a skill's primary/secondary attributes — 0 when the character's attributes were never
    /// imported (every time/rate figure then reads "—" rather than a wrong number).</summary>
    public double SpPerMinute(int primaryAttributeId, int secondaryAttributeId) => Attributes is null
        ? 0
        : Shared.Modules.Skills.SkillPointMath.SkillPointsPerMinute(
            EveUtils.Client.Skills.SkillAttributeLookup.Value(Attributes, primaryAttributeId),
            EveUtils.Client.Skills.SkillAttributeLookup.Value(Attributes, secondaryAttributeId));
}
