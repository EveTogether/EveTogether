using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Opsec;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One line of OPEN ESCALATIONS in Runs (ET-451): the escalation, where, how long it has left, the run and
/// pilot it came from — and START, the one click to its run.</summary>
public sealed partial class OpenEscalationRowViewModel
{
    private readonly Func<OpenEscalationRowViewModel, Task> _start;
    private readonly Func<OpenEscalationRowViewModel, Task> _decline;

    public OpenEscalationRowViewModel(
        OpenEscalationDto escalation, string characterName, DateTime nowUtc, Func<OpenEscalationRowViewModel, Task> start,
        Func<OpenEscalationRowViewModel, Task> decline)
    {
        Escalation = escalation;
        CharacterText = characterName;
        _start = start;
        _decline = decline;
        EscalationSiteText = OpsecText.Mark(escalation.Escalation.SiteName) ?? escalation.Escalation.SiteName;
        EscalationSystemText = OpsecText.Mark(escalation.Escalation.SystemName) ?? "system not recorded";
        SourceSiteText = $"from {OpsecText.Mark(escalation.SourceSiteName) ?? "a run"} · " +
                         $"{escalation.SourceStartedAtUtc.ToLocalTime():d MMM HH:mm} · {characterName}";
        ExpiresText = escalation.InProgressRunId is not null
            ? "escalation run under way"
            : escalation.Escalation.ExpiresAtUtc is { } expiresAtUtc
                ? $"expires in {_Remaining(expiresAtUtc - nowUtc)}"
                : "no deadline recorded";
    }

    public OpenEscalationDto Escalation { get; }

    public string EscalationSiteText { get; }
    public string EscalationSystemText { get; }
    public string SourceSiteText { get; }
    public string CharacterText { get; }
    public string ExpiresText { get; }

    /// <summary>An escalation whose run is already started is flown from its own window, not started twice.</summary>
    public bool CanStart => Escalation.InProgressRunId is null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => _start(this);

    [RelayCommand]
    private Task DeclineAsync() => _decline(this);

    /// <summary>Hours and minutes, the precision the Agency itself shows — never seconds that are stale on arrival.</summary>
    private static string _Remaining(TimeSpan left) => left.TotalHours >= 1
        ? $"{(int)left.TotalHours}h {left.Minutes:00}m"
        : $"{Math.Max(0, left.Minutes)}m";
}
