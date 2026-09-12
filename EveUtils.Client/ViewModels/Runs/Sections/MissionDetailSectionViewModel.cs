using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// MISSION on the detail screen (ET-237): the agent a mission came from, named rather than left as a bare id, and
/// what it handed out — one row per reward form, never one total, since <see cref="RunParameterKey"/> only ever
/// grows and Loyalty Points and Evermarks have no rate to convert into ISK against.
/// </summary>
public sealed partial class MissionDetailSectionViewModel(ISdeAccessor? sde) : RunDetailSection(RunSectionId.Mission, "MISSION")
{
    public ObservableCollection<ActivityRewardRowViewModel> RewardRows { get; } = [];

    [ObservableProperty] private string? _rewardsEmptyText;

    /// <summary>Null for a regular agent's mission, whose capture has no "Report to" line at all (ET-237) — shown
    /// honestly rather than guessed at.</summary>
    [ObservableProperty] private bool _hasAgent;

    [ObservableProperty] private string _agentText = string.Empty;

    [ObservableProperty] private bool _isLevelShown;

    [ObservableProperty] private string _levelText = string.Empty;

    /// <summary>Set when the capture opened with EVE's own warning sentence for an important (storyline) mission
    /// (ET-251) — never derived from anything else, since that sentence is the only signal this project has
    /// measured for it.</summary>
    [ObservableProperty] private bool _isImportantMission;

    [ObservableProperty] private bool _hasBonus;

    [ObservableProperty] private string _bonusValueText = string.Empty;

    /// <summary>Judged against when the run stopped, not against wall-clock "now" — a bonus met minutes before STOP
    /// stays earned no matter how long the activity has sat saved since (ET-237).</summary>
    [ObservableProperty] private bool _isBonusExpired;

    /// <summary>The mission's own target, from the capture's plain "Location" line — never the pilot's own system
    /// (see <c>ActivityDetailSectionViewModel.LocationText</c>). Shown only on an exact SDE match (ET-253).</summary>
    [ObservableProperty] private bool _isMissionLocationShown;

    [ObservableProperty] private string _missionLocationText = string.Empty;

    public override bool HasContent => HasAgent || HasBonus || IsImportantMission || IsMissionLocationShown || RewardRows.Count > 0;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        ActivityRunDetailDto? withAgent = detail.Runs.FirstOrDefault(run => run.AgentId is not null);
        HasAgent = withAgent?.AgentId is not null;
        AgentText = withAgent?.AgentId is { } agentId
            ? sde?.GetAgent(agentId)?.Name ?? $"agent {agentId}"
            : "not stated in this capture";
        IsLevelShown = withAgent?.MissionLevel is not null;
        LevelText = withAgent?.MissionLevel is { } level ? $"Level {level}" : string.Empty;

        IsImportantMission = detail.Parameters.Any(parameter => parameter.ParameterKey == RunParameterKey.ImportantMission);

        RunParameterDto? missionLocation = detail.Parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.MissionLocation);
        string? resolvedMissionLocation = missionLocation?.TypedValue is { Length: > 0 } locationName
            ? sde?.FindSolarSystemByName(locationName)?.Name
            : null;
        IsMissionLocationShown = resolvedMissionLocation is not null;
        MissionLocationText = resolvedMissionLocation ?? string.Empty;

        RunParameterDto[] bonuses = [.. detail.Parameters.Where(parameter => parameter.ParameterKey == RunParameterKey.BonusIsk)];
        RunParameterDto? bonus = bonuses.FirstOrDefault();
        HasBonus = bonus is not null;
        BonusValueText = bonus?.Amount is { } amount ? $"{IskFormat.Number(amount)} ISK" : string.Empty;
        // The rule and the moment TOTAL ISK's rewards contributor judges it by (ET-256): each copy at the stop of the
        // run that carries it — every own toon's run of one mission carries one (ET-210) — and earned once any of
        // them made it in time.
        IsBonusExpired = bonuses.Length > 0 && bonuses.All(copy => MissionBonusDeadline.HasPassed(
            copy.BonusWindowSeconds, copy.ObservedAtUtc,
            detail.Runs.FirstOrDefault(run => run.RunId == copy.RunId)?.StoppedAtUtc ?? DateTime.UtcNow));

        RewardRows.Clear();
        foreach (RunParameterDto parameter in detail.Parameters.Where(_IsRewardRow))
            RewardRows.Add(new ActivityRewardRowViewModel(parameter));

        RewardsEmptyText = !HasAgent && !HasBonus && RewardRows.Count == 0
            ? "No reward was recorded for this mission."
            : null;

        List<string> parts = [];
        if (IsImportantMission)
            parts.Add("important · affects faction standing");
        if (HasAgent)
            parts.Add(IsLevelShown ? $"{AgentText} · {LevelText}" : AgentText);
        if (HasBonus)
            parts.Add(IsBonusExpired ? "bonus expired" : $"{BonusValueText} bonus");
        parts.AddRange(RewardRows.Select(row => $"{row.ValueText} {row.Label}"));
        HeaderSummary = parts.Count > 0 ? string.Join(" · ", parts) : "nothing recorded";
    }

    public override string AbsentReason(string noun) =>
        $"no MISSION — {noun} pays in what it drops, not in a reward agreed beforehand";

    // The bonus gets its own block above with its own expiry treatment; everything else that used to sit under
    // ACTIVITY's Objectives line stays there (ET-237 moved only the agent, not the courier's own cargo counters).
    //
    // Named by what it is, not by what it is not (ET-248): an exclusion list looks complete until the next
    // RunParameterKey lands and is not on it — exactly what happened to RunParameterKey.AbyssalFilament, which
    // showed up here as two "ABYSSAL FILAMENT 3|Dark" reward rows and, through HasContent below, put a MISSION
    // section on an abyssal's detail screen even though RunTypeCatalogue never claims one for that type. A key
    // this list has not caught up with now starts out of the rewards rather than in them.
    private static bool _IsRewardRow(RunParameterDto parameter) =>
        parameter.ParameterKey is RunParameterKey.Isk or RunParameterKey.Bounty or RunParameterKey.FixedPayout
            or RunParameterKey.Escrow or RunParameterKey.LoyaltyPoints or RunParameterKey.Evermarks
            or RunParameterKey.Item or RunParameterKey.Loot or RunParameterKey.Standings or RunParameterKey.Filament
            or RunParameterKey.Unknown;
}
