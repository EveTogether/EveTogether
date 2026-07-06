namespace EveUtils.Client.Dialogs;

/// <summary>One row in the character-picker dialog. <see cref="Enabled"/> is false when the character
/// lacks the scope required for the action (shown but not selectable).</summary>
public sealed record CharacterPickOption(int CharacterId, string Name, string Detail, bool Enabled);
