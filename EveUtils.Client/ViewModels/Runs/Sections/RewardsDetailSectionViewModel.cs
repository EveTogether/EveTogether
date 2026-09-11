using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>REWARDS on the detail screen: one row per reward form, and never one total — the key list keeps growing,
/// and LP and Evermarks have no rate to convert into ISK against.</summary>
public sealed partial class RewardsDetailSectionViewModel() : RunDetailSection(RunSectionId.Rewards, "REWARDS")
{
    public ObservableCollection<ActivityRewardRowViewModel> RewardRows { get; } = [];

    [ObservableProperty] private string? _rewardsEmptyText;

    public override bool HasContent => RewardRows.Count > 0;

    public override void Apply(RunDetailSectionInput input)
    {
        RewardRows.Clear();
        // Everything that is not claimed by another section, rather than a list of the keys known when this was
        // written: RunParameterKey only ever grows, and a key nobody special-cased must show up rather than vanish.
        foreach (RunParameterDto parameter in input.Detail.Parameters.Where(_IsRewardParameter))
            RewardRows.Add(new ActivityRewardRowViewModel(parameter));

        RewardsEmptyText = RewardRows.Count > 0 ? null : "No reward was recorded for this activity.";
        HeaderSummary = RewardRows.Count > 0
            ? string.Join(" · ", RewardRows.Select(row => $"{row.ValueText} {row.Label}"))
            : "nothing recorded";
    }

    public override string AbsentReason(string noun) =>
        $"no REWARDS — {noun} pays in what it drops, not in a reward agreed beforehand";

    private static bool _IsRewardParameter(RunParameterDto parameter) =>
        parameter.ParameterKey is not (RunParameterKey.Escalation or RunParameterKey.EscalationDungeonId
            or RunParameterKey.EscalationSystem or RunParameterKey.EscalationSolarSystemId
            or RunParameterKey.EscalationExpiresAtUtc or RunParameterKey.Smugglers or RunParameterKey.Civilians);
}
