using System.Globalization;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>A copied loot row whose name no SDE type carries yet (ET-460): listed with its amount under the priced
/// rows, and said plainly to be worth nothing for now rather than left out of sight.</summary>
public sealed class UnrecognisedLootRowViewModel(string name, long quantity)
{
    public string Name { get; } = name;

    public long Quantity { get; } = quantity;

    public string NameWithQuantityText => $"{Name} ×{Quantity.ToString("N0", CultureInfo.CurrentCulture)}";

    public string StatusText => "unrecognised · counts as 0";
}
