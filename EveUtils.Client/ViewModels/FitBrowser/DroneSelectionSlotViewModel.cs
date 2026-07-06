using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One drone in a bay stack, drawn as a checkbox in the in-game "Selected:" row on the fit-detail drone bay.
/// Checking the box at position P asks the bay to deploy P drones from this stack; unchecking it recalls to P-1. The
/// window clamps the request to the universal 5-drone limit and the ship's bandwidth and then syncs every box back via
/// <see cref="SetSelected"/>, so a box that cannot deploy snaps back to unchecked.</summary>
public sealed partial class DroneSelectionSlotViewModel : ViewModelBase
{
    private readonly Action<int> _onRequestActive;   // desired active count for the parent stack
    private bool _suppress;

    public int Position { get; }

    [ObservableProperty]
    private bool _isSelected;

    public DroneSelectionSlotViewModel(int position, bool isSelected, Action<int> onRequestActive)
    {
        Position = position;
        _isSelected = isSelected;
        _onRequestActive = onRequestActive;
    }

    /// <summary>Syncs the box from the parent stack without re-triggering the deploy/recall request.</summary>
    public void SetSelected(bool value)
    {
        _suppress = true;
        IsSelected = value;
        _suppress = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_suppress)
            return;
        _onRequestActive(value ? Position : Position - 1);
    }
}
