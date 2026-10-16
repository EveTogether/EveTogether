using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Opsec;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One run that was thrown away and can still be brought back (ET-511): the same Undo the toast offered, for the day
/// after the toast is gone. A restored run lands in UNFINISHED, exactly where Undo puts it.
/// </summary>
public sealed partial class RecentlyDiscardedRunViewModel(
    RecentlyDiscardedRunDto run,
    string characterName,
    Func<RecentlyDiscardedRunViewModel, Task> restore) : ViewModelBase
{
    public Guid RunId { get; } = run.RunId;

    public string TitleText { get; } = $"{(!string.IsNullOrWhiteSpace(run.SiteName)
        ? OpsecText.Mark(run.SiteName)
        : RunTypeCatalogue.For(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId).Space is RunSpace.AbyssalPocket
            ? "Abyssal"
            : "Unnamed site")} · {characterName}";

    public string DiscardedText { get; } = $"thrown away {run.DiscardedAtUtc.ToLocalTime():d MMM HH:mm}";

    public bool IsShowing(RecentlyDiscardedRunDto shown) =>
        shown.RunId == RunId && shown.DiscardedAtUtc == run.DiscardedAtUtc;

    [RelayCommand]
    private Task RestoreAsync() => restore(this);
}
