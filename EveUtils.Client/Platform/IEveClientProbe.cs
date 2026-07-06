using System;
using System.Collections.Generic;

namespace EveUtils.Client.Platform;

/// <summary>
/// What one sweep over the local machine's processes revealed about running EVE clients. Two independent
/// evidence kinds, because no single signal covers every platform:
/// <list type="bullet">
/// <item><see cref="CharacterNames"/> — from client window titles ("EVE - &lt;name&gt;"). Live-accurate (the title
/// follows login/logout/character-switch) but only readable on Windows and Linux/X11.</item>
/// <item><see cref="CharacterIds"/> — from the launcher's <c>/autoSelectCharacter:&lt;id&gt;</c> client argument.
/// Readable everywhere (incl. Wayland and the wine-based macOS client) but it is launch intent, not live state:
/// it goes stale if the player switches characters inside the running client, and is absent on manual logins.</item>
/// </list>
/// A character counts as having an active client when their name OR their ESI id appears in the evidence.
/// </summary>
public sealed record EveClientEvidence(IReadOnlySet<string> CharacterNames, IReadOnlySet<int> CharacterIds)
{
    public static readonly EveClientEvidence Empty = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<int>());

    public bool Matches(string characterName, int characterId) =>
        CharacterNames.Contains(characterName) || (characterId > 0 && CharacterIds.Contains(characterId));

    public bool SameAs(EveClientEvidence other) =>
        CharacterNames.SetEquals(other.CharacterNames) && CharacterIds.SetEquals(other.CharacterIds);
}

/// <summary>One platform's way of finding running EVE clients (window titles and/or client command lines).
/// Implementations are best-effort and must never throw — no evidence simply reads as "no client detected".</summary>
public interface IEveClientProbe
{
    EveClientEvidence Probe();

    /// <summary>Bring the running EVE client for the given character to the foreground (eve-o-preview style:
    /// restore if minimized, then focus). Returns true if a matching window was found and focused. Best-effort —
    /// never throws, and returns false on platforms/states where the window can't be targeted.</summary>
    bool Activate(string characterName);
}

/// <summary>Unsupported platform: never sees a client.</summary>
public sealed class NullEveClientProbe : IEveClientProbe
{
    public EveClientEvidence Probe() => EveClientEvidence.Empty;
    public bool Activate(string characterName) => false;
}
