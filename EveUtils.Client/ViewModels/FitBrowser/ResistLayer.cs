namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>The four damage-type resist percentages of one defensive layer (shield/armor/hull), 0–100.</summary>
public sealed record ResistLayer(double Em, double Thermal, double Kinetic, double Explosive);
