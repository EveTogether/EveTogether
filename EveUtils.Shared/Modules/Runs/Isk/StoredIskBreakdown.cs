using System.Text.Json;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>A breakdown as <c>ActivitySummary.IskContributions</c> holds it: JSON, so a new source needs no column
/// of its own.</summary>
internal static class StoredIskBreakdown
{
    public static string Write(IskBreakdown breakdown) => JsonSerializer.Serialize(breakdown.Contributions);

    // Null is a summary built before breakdowns were stored; the startup rebuild replaces it (IskContributors.Signature).
    public static IskBreakdown Read(string? stored) =>
        stored is null ? IskBreakdown.None : new(JsonSerializer.Deserialize<List<IskContribution>>(stored) ?? []);

    /// <summary>The same breakdown split over the characters who flew it, as
    /// <c>ActivitySummary.IskContributionsByCharacter</c> holds it (ET-296): one entry per character, in the same
    /// shape, so the own share needs no column of its own either — and no rebuild when a character is added to or
    /// taken out of the registry, since which of them are local is decided at the read.</summary>
    public static string WriteByCharacter(IReadOnlyDictionary<long, IskBreakdown> byCharacter) =>
        JsonSerializer.Serialize(byCharacter.ToDictionary(pair => pair.Key, pair => pair.Value.Contributions));

    /// <summary>Null is a summary built before the split was stored — the caller falls back to the activity's own
    /// breakdown rather than reading an own share of nothing, until the startup rebuild fills it in.</summary>
    public static IReadOnlyDictionary<long, IskBreakdown>? ReadByCharacter(string? stored) =>
        stored is null
            ? null
            : (JsonSerializer.Deserialize<Dictionary<long, List<IskContribution>>>(stored) ?? [])
                .ToDictionary(pair => pair.Key, pair => new IskBreakdown(pair.Value));
}
