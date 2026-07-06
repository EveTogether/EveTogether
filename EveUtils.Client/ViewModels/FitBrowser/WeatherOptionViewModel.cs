using EveUtils.Shared.Modules.Dogma;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One entry in the weather/environment picker: the "None" row (<see cref="TypeId"/> null) or an effect beacon.
/// <see cref="Category"/> is the group header ("Wormhole", …), null for "None". <see cref="ToInput"/> yields the engine
/// selection — null for "None", so no weather source is injected and the calculation stays byte-identical.
/// </summary>
public sealed class WeatherOptionViewModel(int? typeId, string label, string? category)
{
    public int? TypeId { get; } = typeId;
    public string Label { get; } = label;
    public string? Category { get; } = category;

    public WeatherInput? ToInput() => TypeId is { } id ? new WeatherInput(id) : null;
}
