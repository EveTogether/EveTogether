using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Commands;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>
/// LINKED RUN on the killmail detail screen (ET-333): the run a loss is linked to, with OPEN RUN plus the same
/// move-to-another-run and unlink actions as LINKED LOSS (ET-331) — both go through
/// <see cref="SetKillmailRunLinkCommand"/>, so this window carries no linking rule of its own (AC6).
/// </summary>
public sealed partial class KillmailLinkedRunViewModel(
    CqrsDispatcher dispatcher, int characterId, int killmailId, Guid activitySummaryId, DateOnly day,
    IReadOnlyList<LinkedLossRunChoice> otherRuns, Func<Guid, DateOnly, Task> openRun, Func<Task> changed)
    : ObservableObject
{
    public required string SiteText { get; init; }

    public required string TimeText { get; init; }

    public required bool IsFailed { get; init; }

    public required string StatusChipText { get; init; }

    public required string ValueText { get; init; }

    public required string ReasonText { get; init; }

    public IReadOnlyList<LinkedLossRunChoice> OtherRuns { get; } = otherRuns;

    public bool HasOtherRuns => OtherRuns.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkToOtherRunCommand))]
    private LinkedLossRunChoice? _selectedRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkToOtherRunCommand), nameof(UnlinkCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _errorText;

    private bool CanLink => SelectedRun is not null && !IsBusy;

    private bool CanUnlink => !IsBusy;

    [RelayCommand]
    private Task OpenRunAsync() => openRun(activitySummaryId, day);

    [RelayCommand(CanExecute = nameof(CanLink))]
    private Task LinkToOtherRunAsync() => _SetRunAsync(SelectedRun?.RunId);

    [RelayCommand(CanExecute = nameof(CanUnlink))]
    private Task UnlinkAsync() => _SetRunAsync(null);

    private async Task _SetRunAsync(Guid? runId)
    {
        IsBusy = true;
        try
        {
            Result stored = await dispatcher.Send(new SetKillmailRunLinkCommand(characterId, killmailId, runId));
            ErrorText = stored.IsSuccess ? null : stored.Messages.FirstOrDefault()?.Text ?? "The link was not changed.";
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorText is null)
        {
            await changed();
        }
    }
}
