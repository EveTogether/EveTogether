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

    [ObservableProperty] private bool _isSelected = true; // default: import all
}
