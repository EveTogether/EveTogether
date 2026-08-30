using System;

namespace EveUtils.Client.Platform;

/// <summary>
/// Whether one of <b>this</b> client's characters is in game right now — and the one place that is decided.
///
/// It exists because three separate things have to agree about it, and would drift apart the moment each worked it
/// out for itself (ET-63/ET-71): the location bootstrap must not record the spot ESI reports for a logged-out
/// character, the WITH FC badge must leave that member out of its denominator, and the member's row must not show
/// a system that is no longer where they are. One verdict, three readers.
///
/// The three-state answer is the whole point. Not seeing a fleet mate's EVE client is not evidence that it is
/// closed — it is on their machine, where this client cannot look. Only a character in our own registry may be
/// called offline; for anyone else the answer is "no idea", which stays ET-70's question to settle.
/// </summary>
public interface ILocalCharacterPresence
{
    /// <summary>
    /// <c>true</c> = one of ours, with a running EVE client. <c>false</c> = one of ours, not in game.
    /// <c>null</c> = not one of this client's characters, so nothing may be inferred either way.
    /// </summary>
    bool? IsInGame(int characterId, string? characterName);

    /// <summary>
    /// Listen for the picture changing — a pilot logging in or out, or the character list itself changing.
    /// Handlers run on the UI thread; dispose to stop, which a screen does when it closes.
    ///
    /// Unlike <see cref="EveSettings.IEveSettingsWatch"/> the announcement carries no payload, because the answer
    /// here is per member rather than one banner's worth of state. It costs a listener nothing to re-ask: the same
    /// no-I/O promise holds, since <see cref="IsInGame"/> is a set lookup over state this already holds.
    /// </summary>
    IDisposable Subscribe(Action handler);
}
