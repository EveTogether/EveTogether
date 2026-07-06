namespace EveUtils.Client.Dialogs;

/// <summary>One row in the share-target server picker: the raw address plus a friendly name.</summary>
public sealed record ServerPickOption(string Address, string DisplayName);
