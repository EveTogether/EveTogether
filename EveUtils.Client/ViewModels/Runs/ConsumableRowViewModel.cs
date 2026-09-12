using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One character's own filament count on CONSUMABLES (ET-249) — a proposal from their fit's hull class, left for
/// the pilot to change. <see cref="Count"/> is editable and never re-derived once the row exists, so an edit is
/// never quietly overwritten by the next clock tick's own proposal.
/// </summary>
public sealed partial class ConsumableRowViewModel(Guid runId, long characterId, string characterName, int? count)
    : ObservableObject
{
    public Guid RunId { get; } = runId;

    public long CharacterId { get; } = characterId;

    public string CharacterText { get; } = characterName;

    [ObservableProperty] private int? _count = count;

    /// <summary>Never "0 ISK" for something simply not priced yet — "no figure yet" until both the count and the
    /// filament's own price are known.</summary>
    [ObservableProperty] private string _valueText = "no figure yet";

    /// <summary>Recomputed by the section whenever its shared unit price, or this row's own count, changes.</summary>
    public void Reprice(decimal? unitPrice) =>
        ValueText = Count is { } count && unitPrice is { } price ? IskFormat.Whole(count * price) : "no figure yet";
}
