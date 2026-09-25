using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// OPTIMISE tab (ET-354): the attribute remap that trains the current TRAINING QUEUE fastest, what a +4/+5 implant
/// set would save instead, and when the next remap is available. The plan-remap scenario is ET-358, not this tab
/// (E4 — "the remap that trains the current queue fastest" — is fully covered here).
/// </summary>
public sealed partial class SkillsOptimiseViewModel : ObservableObject
{
    private const int Plus4SetBonus = 4;
    private const int Plus5SetBonus = 5;

    public ObservableCollection<ImplantAttributeRowViewModel> Implants { get; } = [];
    public ObservableCollection<AttributePairTimeRowViewModel> TimePerAttributePair { get; } = [];

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _bestSplitText = "—";
    [ObservableProperty] private string _queueSavingsText = "—";
    [ObservableProperty] private string _plus4SavingsText = "—";
    [ObservableProperty] private string _plus5SavingsText = "—";
    [ObservableProperty] private string _nextRemapText = "unknown · bonus remaps 0";

    /// <summary>The TRAINING QUEUE REMAP line's text — empty until there is both a queue and imported attributes to
    /// advise on.</summary>
    public string RemapLineText { get; private set; } = "";

    public SkillsOptimiseViewModel(SkillsCharacterSnapshot snapshot, IDogmaDataAccessor? dogma)
    {
        if (snapshot.Attributes is not { } attributes || dogma is null)
        {
            return; // never imported, or dogma unavailable (design-time preview) — every text keeps its default
        }

        var rows = _BuildRows(snapshot);
        if (rows.Count == 0)
        {
            return; // nothing queued — nothing to advise on
        }

        var resolver = new CharacterAttributeResolver(dogma);
        var effective = resolver.Resolve(attributes, snapshot.ImplantTypeIds);
        var baseAttributes = resolver.Base(attributes, snapshot.ImplantTypeIds);
        var implantBonus = effective - baseAttributes;

        var currentTime = AttributeRemapOptimizer.TotalTrainingTime(rows, effective);
        var best = AttributeRemapOptimizer.Best(rows, implantBonus);
        var queueSavings = currentTime - best.TotalTime;
        var plus4Savings = currentTime - AttributeRemapOptimizer.TotalTrainingTime(
            rows, baseAttributes + ImplantSetBonus.Apply(implantBonus, Plus4SetBonus));
        var plus5Savings = currentTime - AttributeRemapOptimizer.TotalTrainingTime(
            rows, baseAttributes + ImplantSetBonus.Apply(implantBonus, Plus5SetBonus));

        HasData = true;
        BestSplitText = string.Join('/', new[]
        {
            best.BaseAttributes.Charisma, best.BaseAttributes.Intelligence, best.BaseAttributes.Memory,
            best.BaseAttributes.Perception, best.BaseAttributes.Willpower
        }.Select(a => a.ToString("0", CultureInfo.InvariantCulture)));
        QueueSavingsText = _SavingsText(queueSavings);
        Plus4SavingsText = _SavingsText(plus4Savings);
        Plus5SavingsText = _SavingsText(plus5Savings);
        NextRemapText = RemapAvailability.Describe(attributes.AccruedRemapCooldownDate, attributes.BonusRemaps, snapshot.Now);
        RemapLineText = $"A remap would save {EveDurationFormatter.Format(queueSavings)}; " +
                         $"a +4 implant set {EveDurationFormatter.Format(plus4Savings)}";

        foreach (var (attributeId, name) in _AttributeIdsByName)
        {
            Implants.Add(new ImplantAttributeRowViewModel(name, implantBonus.For(attributeId)));
        }

        foreach (var pair in rows.GroupBy(r => (r.PrimaryAttributeId, r.SecondaryAttributeId)))
        {
            var pairRows = pair.ToList();
            var time = AttributeRemapOptimizer.TotalTrainingTime(pairRows, best.BaseAttributes + implantBonus);
            TimePerAttributePair.Add(new AttributePairTimeRowViewModel(
                $"{_AttributeName(pair.Key.PrimaryAttributeId)}/{_AttributeName(pair.Key.SecondaryAttributeId)}",
                EveDurationFormatter.Format(time)));
        }
    }

    private static readonly (int AttributeId, string Name)[] _AttributeIdsByName =
    [
        (DogmaAttributeIds.Charisma, "Charisma"),
        (DogmaAttributeIds.Intelligence, "Intelligence"),
        (DogmaAttributeIds.Memory, "Memory"),
        (DogmaAttributeIds.Perception, "Perception"),
        (DogmaAttributeIds.Willpower, "Willpower"),
    ];

    private static string _AttributeName(int attributeId) =>
        _AttributeIdsByName.FirstOrDefault(a => a.AttributeId == attributeId).Name ?? "?";

    private static string _SavingsText(TimeSpan savings) =>
        savings > TimeSpan.Zero ? $"saves {EveDurationFormatter.Format(savings)}" : "already optimal";

    /// <summary>The TRAINING QUEUE's not-yet-finished rows as remap rows — same level-SP delta TRAINING QUEUE itself
    /// sums for SP IN QUEUE, keyed to the skill's primary/secondary attribute from the SDE.</summary>
    private static IReadOnlyList<RemapTrainingRow> _BuildRows(SkillsCharacterSnapshot snapshot)
    {
        var skillsById = snapshot.Sde.GetGroupsByCategory(16)
            .SelectMany(g => snapshot.Sde.GetSkillsInGroup(g.GroupId))
            .ToDictionary(s => s.TypeId, s => s);

        var rows = new List<RemapTrainingRow>();
        foreach (var entry in snapshot.Queue.Where(e => e.FinishDate is null || e.FinishDate > snapshot.Now))
        {
            if (!skillsById.TryGetValue(entry.SkillTypeId, out var skill))
            {
                continue;
            }

            var remainingSp = SkillPointMath.SkillPointsForLevel(skill.Rank, entry.FinishedLevel)
                             - SkillPointMath.SkillPointsForLevel(skill.Rank, entry.FinishedLevel - 1);
            rows.Add(new RemapTrainingRow(skill.PrimaryAttributeId, skill.SecondaryAttributeId, remainingSp));
        }

        return rows;
    }
}
