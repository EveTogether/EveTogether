using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Identity;

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
/// called offline; for anyone else the answer is "no idea". ET-70 answers that half elsewhere — from the fleet
/// stream and from its silence — and publishes this verdict about our own pilots onto it.
/// </summary>
public interface ILocalCharacterPresence
{
    /// <summary>
    /// <c>true</c> = one of ours, with a running EVE client. <c>false</c> = one of ours, not in game.
    /// <c>null</c> = not one of this client's characters, so nothing may be inferred either way.
    /// </summary>
    bool? IsInGame(int characterId, string? characterName);

    /// <summary>
    /// The same verdict for a character we hold only an id for — the metric publisher's case, which is handed a
    /// participating character id and no name. Asking by id alone is not the same question asked with a null name:
    /// on Windows the only presence evidence is the client's window title, so the name has to be resolved from the
    /// registry first or a pilot who is plainly flying reads as logged off (the ET-71 trap). This resolves it here,
    /// where that registry snapshot already lives, rather than growing a second copy of it at the call site.
    /// </summary>
    bool? IsInGame(int characterId);

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

/// <summary>
/// Which of this client's characters are worth asking "who is flying this?" about: the ones with an EVE client
/// actually up. Counted per CHARACTER, not per running client — one sitting on the login screen is a process with
/// no pilot behind it.
///
/// One rule, two askers: the run window at START (<c>_ResolveCharacterAsync</c>) and the fleet-run offer before it
/// opens any window (<c>FleetRunWindowPresenter</c>). They differ only in what an empty answer means, and each
/// decides that for itself — seeing nobody is not knowing rather than nobody, and at START a character is required
/// while at the offer it is not.
/// </summary>
public static class InGameCharacters
{
    public static List<Character> Among(IReadOnlyList<Character> known, ILocalCharacterPresence? presence) =>
        presence is null
            ? []
            : [.. known.Where(character => character.EsiCharacterId is { } id
                                           && presence.IsInGame(id, character.Name) is true)];
}
