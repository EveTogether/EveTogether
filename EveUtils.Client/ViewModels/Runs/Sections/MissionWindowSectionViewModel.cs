using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
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

    // Once the bonus window has passed it drops out of TOTAL ISK rather than stay silently counted (ET-237) — this
    // is the only figure this section removes; the plain ISK and the mission's own stated Bounty line keep counting
    // (or not) exactly as TotalIskCalculator.RewardIsk already decided for every type.
    public override decimal ExpiredBonusIsk => IsBonusExpired ? _bonusAmount ?? 0m : 0m;

    public override void Load(IReadOnlyList<SettingDto>? settings) => _SyncWithPendingParameters();

    // The deadline is measured, not assumed (ET-237): the capture states a window "remaining" at the moment it was
    // copied (EVE's own journal counts down), so the deadline is that copy moment plus the stated window — never a
    // window counted from mission accept, which this run has no timestamp for anyway. Frozen at
    // Context.EffectiveStopUtc once the run stops, so sitting on a stopped window past the deadline before SAVE
    // cannot flip a bonus that was actually earned into an expired one.
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

        RunParameterInput? bonus = parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.BonusIsk);
        if (bonus is not null)
        {
            _bonusAmount = bonus.Amount;
            _bonusDeadlineUtc = bonus.BonusWindowSeconds is { } seconds ? bonus.ObservedAtUtc.AddSeconds(seconds) : null;
        }

        foreach (RunParameterInput parameter in parameters.Where(parameter => parameter.ParameterKey != RunParameterKey.BonusIsk))
            RewardRows.Add(new ActivityRewardRowViewModel(new RunParameterDto(Guid.Empty, parameter.ParameterKey,
                parameter.TypedValue, parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc)));

        RewardsEmptyText = RewardRows.Count == 0 && !HasBonus ? "No reward was recorded for this mission." : null;
        RefreshSummary();
    }

    private static string _FormatRemaining(TimeSpan remaining) =>
        (remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining).ToString(@"h\:mm\:ss");
}
