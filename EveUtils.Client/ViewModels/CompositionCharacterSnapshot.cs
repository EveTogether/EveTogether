using System.Collections.Generic;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Client.ViewModels;

public sealed record CompositionCharacterSnapshot(
    string Name,
    bool HasSkillsScope,
    bool HasQueueScope,
    IReadOnlyDictionary<int, int> Levels,
    CharacterAttributeSet? Attributes,
    IReadOnlyList<CharacterSkillQueueEntry> Queue);
