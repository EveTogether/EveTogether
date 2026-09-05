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
    Signature,
    Mission
}

/// <summary>
/// A recognised clipboard payload handed to the subscribers of <see cref="ClipboardWatchService"/>. It carries
/// the raw text because a subscriber decides for itself what to make of it (import the fit, read the loot rows);
/// the watcher itself neither parses nor stores it.
/// </summary>
/// <param name="CopiedByCharacter">Who had OS focus at the moment of the OS's own clipboard-change notification —
/// read as early in that notification as possible (ET-138). Null means "unknown", not "nobody": a different app,
/// a platform this hasn't been built for, or a genuinely ambiguous read all collapse to the same null rather than
/// a guess. Nothing here decides what to do with it — using it to resolve a run's pilot is still open (ET-130).</param>
public sealed record ClipboardCapture(ClipboardShape Shape, string Text, string? CopiedByCharacter = null);
