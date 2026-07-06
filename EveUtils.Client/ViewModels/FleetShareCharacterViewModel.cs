using System.Collections.Generic;

namespace EveUtils.Client.ViewModels;

/// <summary>One character's metric rows in the per-fleet sharing dialog (or the shared "all characters" set).</summary>
public sealed class FleetShareCharacterViewModel(int characterId, string name, IReadOnlyList<FleetMetricShareRowViewModel> metrics)
{
    public int CharacterId { get; } = characterId;
    public string Name { get; } = name;
    public IReadOnlyList<FleetMetricShareRowViewModel> Metrics { get; } = metrics;
}
