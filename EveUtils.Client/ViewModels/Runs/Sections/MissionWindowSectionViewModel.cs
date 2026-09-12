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
using EveUtils.Shared.Modules.Settings.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// MISSION in the run window (ET-237): the agent behind the mission, and what it hands out — read from
/// <see cref="IRunWindowContext.PendingParameters"/>, the reward lines the clipboard capture already carried at
/// accept time, rather than collected as the run goes the way a site's loot is.
///
/// Synced against <see cref="IRunWindowContext.PendingParameters"/> by reference rather than built once: the
/// constructor's own first <see cref="Refresh"/> (<c>ActivityWindowViewModel</c>'s constructor calls it after
/// <see cref="RunSectionModules"/> builds every section) runs before the object initialiser that hands this window
/// its parameters (<c>ClipboardMissionOffer._StartRunAsync</c>) — reading them there would see only the still-empty
/// default and, with a one-shot build, never see the real list at all. Comparing the reference on every tick instead
/// costs nothing measurable and needs no second hook to remember to call.
/// </summary>
public sealed partial class MissionWindowSectionViewModel : RunWindowSection
{
    private DateTime? _bonusDeadlineUtc;
    private decimal? _bonusAmount;
    private string? _missionLocationSystemName;
    private IReadOnlyList<RunParameterInput>? _syncedWith;

    public MissionWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Mission, "MISSION")
    {
    }

    // ── The agent ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Null for a regular agent's mission, whose capture has no "Report to" line at all (ET-237) — shown
    /// honestly rather than guessed at.</summary>
    public bool HasAgent => Context.MissionAgentId is not null;

    public string AgentText => Context.MissionAgentId is { } agentId
        ? Context.Services.GetService<ISdeAccessor>()?.GetAgent(agentId)?.Name ?? $"agent {agentId}"
        : "not stated in this capture";

    public bool IsLevelShown => Context.MissionLevel is not null;

    public string LevelText => Context.MissionLevel is { } level ? $"Level {level}" : string.Empty;

    // ── The mission's own destination ─────────────────────────────────────────────────────────────

    /// <summary>The mission's own target, from the capture's plain "Location" line — never the pilot's own system
    /// (see <c>ActivityDetailSectionViewModel.LocationText</c>). Shown only on an exact SDE match (ET-253); an
    /// unrecognised name has nothing else worth showing, unlike an escalation's destination, which keeps the raw
    /// text.</summary>
    public bool IsMissionLocationShown => MissionLocationText.Length > 0;

    public string MissionLocationText => _missionLocationSystemName is { Length: > 0 } name
        ? Context.Services.GetService<ISdeAccessor>()?.FindSolarSystemByName(name)?.Name ?? string.Empty
        : string.Empty;

    /// <summary>Set when the capture opened with EVE's own warning sentence for an important (storyline) mission
    /// (ET-251) — never derived from anything else, since that sentence is the only signal this project has
    /// measured for it.</summary>
    public bool IsImportantMission { get; private set; }

    // ── The rewards ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every reward line but the bonus, which gets its own countdown block below — LP, Evermarks, items and
    /// a line this build does not yet classify (<see cref="RunParameterKey.Unknown"/>) shown as its own raw text
    /// rather than dropped (ET-208).</summary>
    public ObservableCollection<ActivityRewardRowViewModel> RewardRows { get; } = [];

    [ObservableProperty] private string? _rewardsEmptyText;

    // ── The bonus ──────────────────────────────────────────────────────────────────────────────────

    // Amber inside the last five minutes — the same warning band the run's own clock already uses
    // (ActivityWindowViewModel.WarningAt), kept as this section's own constant rather than reached for across
    // classes for one shared number.
    private static readonly TimeSpan BonusWarningAt = TimeSpan.FromMinutes(5);

    public bool HasBonus => _bonusAmount is not null;

    public string BonusValueText => _bonusAmount is { } amount ? $"{IskFormat.Number(amount)} ISK" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBonusOk))]
    private bool _isBonusExpired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBonusOk))]
    private bool _isBonusWarning;

    public bool IsBonusOk => !IsBonusExpired && !IsBonusWarning;

    [ObservableProperty] private string _bonusCountdownText = string.Empty;

    public override void Load(IReadOnlyList<SettingDto>? settings) => _SyncWithPendingParameters();

    // Judged by MissionBonusDeadline, the rule TOTAL ISK's rewards contributor judges the same bonus by (ET-256), and
    // frozen at Context.EffectiveStopUtc once the run stops, so sitting on a stopped window past the deadline before
    // SAVE cannot flip a bonus that was actually earned into an expired one.
    public override void Refresh(DateTime nowUtc)
    {
        _SyncWithPendingParameters();
        if (_bonusDeadlineUtc is not { } deadline)
            return;

        DateTime evaluatedAt = Context.EffectiveStopUtc ?? nowUtc;
        TimeSpan remaining = deadline - evaluatedAt;
        IsBonusExpired = remaining <= TimeSpan.Zero;
        IsBonusWarning = !IsBonusExpired && remaining <= BonusWarningAt;
        BonusCountdownText = IsBonusExpired ? "bonus expired" : $"expires in {_FormatRemaining(remaining)}";
    }

    public override void RefreshSummary()
    {
        List<string> parts = [];
        if (IsImportantMission)
            parts.Add("important · affects faction standing");
        if (HasBonus)
            parts.Add(IsBonusExpired ? "bonus expired" : $"{BonusValueText} bonus");
        parts.AddRange(RewardRows.Select(row => $"{row.ValueText} {row.Label}"));
        HeaderSummary = parts.Count > 0 ? string.Join(" · ", parts) : "nothing recorded";
    }

    private void _SyncWithPendingParameters()
    {
        IReadOnlyList<RunParameterInput> parameters = Context.PendingParameters;
        if (ReferenceEquals(parameters, _syncedWith))
            return;

        _syncedWith = parameters;
        _bonusAmount = null;
        _bonusDeadlineUtc = null;
        RewardRows.Clear();

        IsImportantMission = parameters.Any(parameter => parameter.ParameterKey == RunParameterKey.ImportantMission);
        _missionLocationSystemName = parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.MissionLocation)?.TypedValue;

        RunParameterInput? bonus = parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.BonusIsk);
        if (bonus is not null)
        {
            _bonusAmount = bonus.Amount;
            _bonusDeadlineUtc = MissionBonusDeadline.Of(bonus.BonusWindowSeconds, bonus.ObservedAtUtc);
        }

        foreach (RunParameterInput parameter in parameters.Where(parameter =>
            parameter.ParameterKey is not (RunParameterKey.BonusIsk or RunParameterKey.ImportantMission or RunParameterKey.MissionLocation)))
            RewardRows.Add(new ActivityRewardRowViewModel(new RunParameterDto(Guid.Empty, parameter.ParameterKey,
                parameter.TypedValue, parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc)));

        RewardsEmptyText = RewardRows.Count == 0 && !HasBonus ? "No reward was recorded for this mission." : null;
        RefreshSummary();
    }

    private static string _FormatRemaining(TimeSpan remaining) =>
        (remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining).ToString(@"h\:mm\:ss");
}
