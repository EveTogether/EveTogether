using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Opsec;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>One escalation in ESCALATION on the detail screen (ET-451): where it leads, when it expires, how it
/// ended, and the actions on it — start its run, tick it off by hand, or reopen it.</summary>
public sealed partial class EscalationEntryViewModel : ObservableObject
{
    private readonly Func<EscalationEntryViewModel, Task> _start;
    private readonly Func<EscalationEntryViewModel, EscalationOutcome?, Task> _setOutcome;
    private readonly Func<EscalationEntryViewModel, Task> _openCompletedRun;
    private readonly Func<EscalationEntryViewModel, Task> _change;
    private readonly Func<EscalationEntryViewModel, Task> _linkToRun;

    public EscalationEntryViewModel(
        Guid sourceRunId, RunEscalationDto escalation, bool isOwn, DateTime nowUtc,
        Func<EscalationEntryViewModel, Task> start,
        Func<EscalationEntryViewModel, EscalationOutcome?, Task> setOutcome,
        Func<EscalationEntryViewModel, Task> openCompletedRun,
        Func<EscalationEntryViewModel, Task> change,
        Func<EscalationEntryViewModel, Task> linkToRun)
    {
        SourceRunId = sourceRunId;
        Escalation = escalation;
        _change = change;
        _linkToRun = linkToRun;
        _start = start;
        _setOutcome = setOutcome;
        _openCompletedRun = openCompletedRun;

        EscalationText = OpsecText.Mark(escalation.SiteName);
        EscalationSystemText = OpsecText.Mark(escalation.SystemName);
        EscalationObservedText =
            $"read from the Agency at {escalation.RegisteredAtUtc.ToLocalTime():HH:mm} on " +
            $"{escalation.RegisteredAtUtc.ToLocalTime():d MMM}";
        EscalationExpiresAtText = escalation.ExpiresAtUtc is { } expiresAtUtc
            ? $"expires {expiresAtUtc.ToLocalTime():HH:mm} on {expiresAtUtc.ToLocalTime():d MMM}"
            : null;
        IsOpen = escalation.IsOpenAt(nowUtc);
        StatusText = escalation.Outcome switch
        {
            EscalationOutcome.Completed when escalation.CompletedByRunId is not null => "completed by its escalation run",
            EscalationOutcome.Completed => "marked completed",
            EscalationOutcome.Expired => "marked expired",
            _ when !IsOpen => "past its deadline",
            _ => "open"
        };
        // Only this machine's own pilot can fly it or tick it off: the run is theirs to change (ET-214).
        CanAct = isOwn;
    }

    public Guid SourceRunId { get; }

    public RunEscalationDto Escalation { get; }

    public string? EscalationText { get; }
    public string? EscalationSystemText { get; }
    public string EscalationObservedText { get; }
    public string? EscalationExpiresAtText { get; }
    public string StatusText { get; }

    [ObservableProperty] private string? _escalationJumpsText;
    [ObservableProperty] private string? _escalationJumpsEmptyText;

    /// <summary>Why the last action did not happen — a running run on the character, a refused start.</summary>
    [ObservableProperty] private string? _actionMessage;

    public bool IsOpen { get; }

    public bool CanAct { get; }

    public bool CanStart => CanAct && IsOpen;

    public bool CanMarkDone => CanAct && Escalation.Outcome is null;

    public bool CanReopen => CanAct && Escalation.Outcome is not null;

    public bool HasCompletedRun => Escalation.CompletedByRunId is not null;

    /// <summary>The destination's solarSystemId as stored, for the jump count — null when the SDE did not know the
    /// name at registration.</summary>
    public int? DestinationSystemId => Escalation.SolarSystemId;


    [RelayCommand]
    private Task ChangeAsync() => _change(this);

    [RelayCommand]
    private Task StartEscalationRunAsync() => _start(this);

    [RelayCommand]
    private Task LinkToRunAsync() => _linkToRun(this);

    [RelayCommand]
    private Task MarkDoneAsync() => _setOutcome(this, EscalationOutcome.Completed);

    [RelayCommand]
    private Task MarkExpiredAsync() => _setOutcome(this, EscalationOutcome.Expired);

    [RelayCommand]
    private Task ReopenAsync() => _setOutcome(this, null);

    [RelayCommand]
    private Task OpenCompletedRunAsync() => _openCompletedRun(this);
}
