namespace EveUtils.Client.Clipboard;

/// <summary>
/// One row of a copied scan-signature list. <see cref="Group"/> and <see cref="Name"/> are null below their reveal
/// threshold — that is the normal, not-yet-fully-scanned case, not a parsing failure.
/// </summary>
public sealed record ClipboardSignatureRow(string SignatureId, string? Group, string? Name);
