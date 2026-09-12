using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One run that was stopped and then left there — never saved, never thrown away. Its own band above the days and
/// never a row between them: what is listed under a day is what was saved, and mixing the two would make a stopped
/// run count towards an evening it was never committed to (ET-179 AC-3).
///
/// SAVE and DELETE were the only two ways out while <c>Stopped</c> meant the run window that owned the row was long
/// gone — true for a run stopped on purpose, never true any more for one this app itself stopped at startup because
/// the previous process quit or crashed with it still going (ET-254). RESUME reopens the run window on exactly that
/// row and picks the clock back up, the same pause STOP/START already is inside an open window; it is hidden for an
/// abyssal whose pocket has already collapsed (<see cref="CanResume"/>), where there is nothing left to step back
/// into.
/// </summary>
public sealed partial class UnfinishedRunViewModel(
    UnfinishedRunDto run,
    string characterName,
    Func<UnfinishedRunViewModel, Task> save,
    Func<UnfinishedRunViewModel, Task> delete,
    Func<UnfinishedRunViewModel, Task> resume) : ViewModelBase
{
    private readonly UnfinishedRunDto _source = run;

    public Guid RunId { get; } = run.RunId;

    public long CharacterId { get; } = run.CharacterId;

    public ActivityKind ActivityKind { get; } = run.ActivityKind;

    public DateTime StartedAtUtc { get; } = run.StartedAtUtc;

    public DateTime? StoppedAtUtc { get; } = run.StoppedAtUtc;

    /// <summary>Whether RESUME may be offered at all (ET-254 AC-5). An abyssal pocket collapses
    /// <see cref="AbyssalSpace.RunLimit"/> after the pilot's own entry — not after it was stopped, which for a run
    /// this app only just stopped at startup are the same anchor anyway (<see cref="StartedAtUtc"/>) — so a row
    /// older than that is one RESUME can no longer mean anything for: the ship and the pod are already gone.
    /// Every other type has no such clock, so the pilot's own account decides instead.</summary>
    public bool CanResume { get; } =
        RunTypeCatalogue.For(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId).Space
            is not RunSpace.AbyssalPocket
        || DateTime.UtcNow - run.StartedAtUtc <= AbyssalSpace.RunLimit;

    public string CharacterText { get; } = characterName;

    // An abyssal never has a site name to fall back to — this reads the type's own honest name instead of "Unnamed
    // site" (ET-241). Its tier and weather are only ever persisted at SAVE (ActivityWindowSectionViewModel.AddToSave),
    // so an unfinished row — stopped, not yet saved — always reads the plain "Abyssal" here, same as a site with no
    // name yet reads "Unnamed site" until it has one.
    public string SiteText { get; } = !string.IsNullOrWhiteSpace(run.SiteName)
        ? run.SiteName
        : RunTypeCatalogue.For(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId).Space
            is RunSpace.AbyssalPocket
            ? "Abyssal"
            : "Unnamed site";

    /// <summary>What it was and who flew it, as one run of text — two of these rows differ by the pilot as often as
    /// by the site, so neither may be the one that gets trimmed away first.</summary>
    public string TitleText =>
        $"{SiteText} · {CharacterText}";

    /// <summary>When it was left, and what it was — the only two facts that tell one stale row from the next.</summary>
    public string StoppedText { get; } =
        $"{ActivityOverviewRowViewModel.KindLabel(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId)} · " + (run.StoppedAtUtc is { } stoppedAtUtc
            ? $"stopped {stoppedAtUtc.ToLocalTime():d MMM HH:mm}"
            : $"started {run.StartedAtUtc.ToLocalTime():d MMM HH:mm}, never stopped");

    /// <summary>Counted in whole hours rather than <c>hh:mm:ss</c>: a run left standing for a day and a half is
    /// exactly what lands here, and a wrapped clock would read it back as an hour and a half.</summary>
    public string DurationText { get; } = _Elapsed((run.StoppedAtUtc ?? run.StartedAtUtc) - run.StartedAtUtc);

    /// <summary>What this run earned so far, out of the same sum the run window's own TOTAL ISK and a saved
    /// activity's TOTAL ISK are made of (<see cref="UnfinishedRunDto.TotalIsk"/>) — shown honestly rather than left
    /// blank when there is nothing yet. A real, known zero reads "0 ISK", and a run that lost more than it made reads
    /// below zero the way the saved activity will (ET-256) rather than as a zero; an unanswerable one — captured loot
    /// with no known price, and nothing else to go on — says so in words instead of pretending to be a zero it might
    /// not be (<see cref="UnfinishedRunDto.TotalIskUnknown"/>, ET-217 review).
    /// </summary>
    public string TotalIskText { get; } =
        run.TotalIskUnknown ? "not priced yet" : IskFormat.Whole(run.TotalIsk);

    /// <summary>Whether <see cref="TotalIskText"/> is that unanswerable case — read by the view so the two never
    /// look alike: a real amount is a figure worth noticing, a shrug is not.</summary>
    public bool TotalIskUnknown { get; } = run.TotalIskUnknown;

    /// <summary>Whether this row already says everything <paramref name="shown"/> would, so a refresh can keep this
    /// instance — and the SAVE or DELETE the pointer is resting on — where it is (ET-222).</summary>
    public bool IsShowing(UnfinishedRunDto shown) => shown == _source;

    [RelayCommand]
    private Task SaveAsync() => save(this);

    [RelayCommand]
    private Task DeleteAsync() => delete(this);

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => resume(this);

    private static string _Elapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
