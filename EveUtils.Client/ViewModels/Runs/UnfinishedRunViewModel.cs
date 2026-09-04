using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One run that was stopped and then left there — never saved, never thrown away. Its own band above the days and
/// never a row between them: what is listed under a day is what was saved, and mixing the two would make a stopped
/// run count towards an evening it was never committed to (ET-179 AC-3).
///
/// SAVE and DELETE are the only two ways out, because <c>Stopped</c> is where these rows have been sitting: the run
/// window that owned both is long gone, so there is nothing left to reopen them into.
/// </summary>
public sealed partial class UnfinishedRunViewModel(
    UnfinishedRunDto run,
    string characterName,
    Func<UnfinishedRunViewModel, Task> save,
    Func<UnfinishedRunViewModel, Task> delete) : ViewModelBase
{
    public Guid RunId { get; } = run.RunId;

    public DateTime? StoppedAtUtc { get; } = run.StoppedAtUtc;

    public string CharacterText { get; } = characterName;

    public string SiteText { get; } = string.IsNullOrWhiteSpace(run.SiteName) ? "Unnamed site" : run.SiteName;

    /// <summary>What it was and who flew it, as one run of text — two of these rows differ by the pilot as often as
    /// by the site, so neither may be the one that gets trimmed away first.</summary>
    public string TitleText =>
        $"{SiteText} · {CharacterText}";

    /// <summary>When it was left, and what it was — the only two facts that tell one stale row from the next.</summary>
    public string StoppedText { get; } =
        $"{ActivityOverviewRowViewModel.KindLabel(run.ActivityKind)} · " + (run.StoppedAtUtc is { } stoppedAtUtc
            ? $"stopped {stoppedAtUtc.ToLocalTime():d MMM HH:mm}"
            : $"started {run.StartedAtUtc.ToLocalTime():d MMM HH:mm}, never stopped");

    /// <summary>Counted in whole hours rather than <c>hh:mm:ss</c>: a run left standing for a day and a half is
    /// exactly what lands here, and a wrapped clock would read it back as an hour and a half.</summary>
    public string DurationText { get; } = _Elapsed((run.StoppedAtUtc ?? run.StartedAtUtc) - run.StartedAtUtc);

    [RelayCommand]
    private Task SaveAsync() => save(this);

    [RelayCommand]
    private Task DeleteAsync() => delete(this);

    private static string _Elapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
