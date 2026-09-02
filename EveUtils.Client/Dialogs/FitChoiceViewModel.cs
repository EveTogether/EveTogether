using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.Dialogs;

/// <summary>One selectable fit row in the fit-import dialog.</summary>
public partial class FitChoiceViewModel(EsiFitting fit) : ObservableObject
{
    public int FittingId  { get; } = fit.FittingId;
    public string Name    { get; } = fit.Name;
    public int ShipTypeId { get; } = fit.ShipTypeId;
    public int ItemCount  { get; } = fit.Items.Count;

    // Off by default: the dialog says "tick the ones to store locally", and a fit the user never saw (it was
    // filtered out) must not ride along on the import (ET-145).
    [ObservableProperty] private bool _isSelected;
}
