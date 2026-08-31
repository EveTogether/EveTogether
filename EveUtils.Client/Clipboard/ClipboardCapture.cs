namespace EveUtils.Client.Clipboard;

/// <summary>
/// What a clipboard payload was recognised as. Everything that is not on this list is
/// <see cref="Unrecognised"/> and is dropped without being kept, buffered or logged.
/// </summary>
public enum ClipboardShape
{
    Unrecognised,
    Fit,
    Inventory,
    Signature
}

/// <summary>
/// A recognised clipboard payload handed to the subscribers of <see cref="ClipboardWatchService"/>. It carries
/// the raw text because a subscriber decides for itself what to make of it (import the fit, read the loot rows);
/// the watcher itself neither parses nor stores it.
/// </summary>
public sealed record ClipboardCapture(ClipboardShape Shape, string Text);
