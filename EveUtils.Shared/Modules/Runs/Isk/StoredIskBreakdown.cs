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
}
