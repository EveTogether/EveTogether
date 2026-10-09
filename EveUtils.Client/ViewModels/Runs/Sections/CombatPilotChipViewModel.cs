using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// One pilot of the activity in COMBAT's chip row (ET-468): a pilot whose run kept its combat can be picked, any other
/// says why there is nothing to pick.
/// </summary>
public sealed partial class CombatPilotChipViewModel(Guid runId, string text, bool isAvailable, Action<Guid> show, string? tooltip = null)
    : ObservableObject
{
    public Guid RunId { get; } = runId;
    public string Text { get; } = text;
    public bool IsAvailable { get; } = isAvailable;
    public string? Tooltip { get; } = tooltip;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private void Show() => show(RunId);
}
