namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A row in the STORAGE panel: a bay's display name and its formatted capacity (e.g. "12,500 m³").</summary>
public sealed record StorageBayViewModel(string Name, string Volume);
