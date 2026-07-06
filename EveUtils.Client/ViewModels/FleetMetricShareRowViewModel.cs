using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One metric row in the per-fleet sharing dialog: a label + a three-way choice bound to a ComboBox
/// (0 = use global default, 1 = share, 2 = don't share).
/// </summary>
public sealed partial class FleetMetricShareRowViewModel(MetricKind kind, string label, int choiceIndex) : ObservableObject
{
    public MetricKind Kind { get; } = kind;
    public string Label { get; } = label;

    [ObservableProperty] private int _choiceIndex = choiceIndex;
}
