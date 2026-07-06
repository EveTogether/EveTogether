namespace EveUtils.Client.ViewModels;

/// <summary>One per-fit minimum within a role's two-level fill overview: the entry's fit name and its
/// "filled / minimum" tally (e.g. "Guardian 1 / 3"). Only entries that carry a per-fit minimum appear here.</summary>
public sealed record CompositionFillEntryViewModel(string FitName, string Count);
