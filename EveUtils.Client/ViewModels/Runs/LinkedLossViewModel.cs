using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Commands;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>A run a loss may be moved to, as the picker names it.</summary>
public sealed record LinkedLossRunChoice(Guid RunId, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// One linked loss in LINKED LOSS (ET-331) and the pilot's two actions on it: move it to another run, or unlink it.
/// Both go through <see cref="SetKillmailRunLinkCommand"/>, which rebuilds the activities and marks the link Manual.
/// </summary>
public sealed partial class LinkedLossViewModel(
    CqrsDispatcher dispatcher, int characterId, int killmailId, IReadOnlyList<LinkedLossRunChoice> otherRuns,
    Func<Task> changed) : ObservableObject
{
    public required string ShipText { get; init; }

    public required string FitText { get; init; }

    public required string TimeText { get; init; }

    public required string FinalBlowText { get; init; }

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
