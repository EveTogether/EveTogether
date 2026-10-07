namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// One figure in COMBAT's grid (ET-468): its label, the figure, and the line under it that says what it is made of.
/// </summary>
public sealed record CombatTileViewModel(string Label, string Value, string Note);
