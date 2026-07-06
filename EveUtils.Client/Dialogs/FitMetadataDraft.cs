namespace EveUtils.Client.Dialogs;

/// <summary>The user-editable metadata of a local fit (fit-metadata): its name plus optional free-text description and
/// comma-separated tags. Carried both into the edit dialog (current values) and out of it (the edited values).</summary>
public sealed record FitMetadataDraft(string Name, string? Description, string? Tags);
