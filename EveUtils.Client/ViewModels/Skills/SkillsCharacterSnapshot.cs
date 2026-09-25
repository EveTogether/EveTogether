using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// Everything CATALOGUE, TRAINING QUEUE and OPTIMISE (ET-16, ET-354) need for one character, read once off the UI
/// thread when the character changes and shared by every tab — avoids threading separate reads through each
/// view-model. <see cref="ImplantTypeIds"/> defaults to none for callers that only need the first two tabs.
/// </summary>
public sealed record SkillsCharacterSnapshot(
    ISdeAccessor Sde,
    IReadOnlyDictionary<int, int> Levels,
    IReadOnlyList<CharacterSkillQueueEntry> Queue,
    CharacterAttributes? Attributes,
    DateTimeOffset Now,
    IReadOnlyList<int>? ImplantTypeIds = null)
{
    public IReadOnlyList<int> ImplantTypeIds { get; } = ImplantTypeIds ?? [];

    public int LevelOf(int skillTypeId) => Levels.TryGetValue(skillTypeId, out var level) ? level : 0;

    /// <summary>The skill and target level actively training right now — the queue's head entry, and only while it
    /// carries a FinishDate (a paused queue trains nothing). Null with an empty or fully paused queue.</summary>
    public (int SkillTypeId, int Level)? TrainingHead
    {
        get
        {
            var head = Queue.Where(e => e.FinishDate is not null).OrderBy(e => e.QueuePosition).FirstOrDefault();
            return head is null ? null : (head.SkillTypeId, head.FinishedLevel);
        }
    }

    /// <summary>SP/min for a skill's primary/secondary attributes — 0 when the character's attributes were never
    /// imported (every time/rate figure then reads "—" rather than a wrong number).</summary>
    public double SpPerMinute(int primaryAttributeId, int secondaryAttributeId) => Attributes is null
        ? 0
        : Shared.Modules.Skills.SkillPointMath.SkillPointsPerMinute(
            EveUtils.Client.Skills.SkillAttributeLookup.Value(Attributes, primaryAttributeId),
            EveUtils.Client.Skills.SkillAttributeLookup.Value(Attributes, secondaryAttributeId));
}
