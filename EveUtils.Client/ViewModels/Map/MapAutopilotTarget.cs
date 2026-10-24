using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>One of your own characters as the autopilot sees it: whether it allowed the scope and whether it is known to be out of game.</summary>
public sealed record MapAutopilotCandidate(int CharacterId, string Name, bool HasScope, bool IsOffline)
{
    /// <summary>Known-offline characters stay in the list but are dimmed: the position source may lag, so they are not blocked.</summary>
    public double Opacity => IsOffline ? 0.5 : 1;

    public string Label => IsOffline ? Name + " · offline" : Name;
}

public enum MapAutopilotPlan
{
    /// <summary>One character has the scope: send to it.</summary>
    Send,

    /// <summary>Several characters have the scope: ask which one.</summary>
    Choose,

    /// <summary>The character that would be used has not allowed the scope: offer ALLOW…</summary>
    Allow,

    /// <summary>Nobody has the scope and there are several characters: ask which one should allow it.</summary>
    ChooseToAllow,

    /// <summary>No own character is signed in to ESI.</summary>
    NoCharacter
}

/// <summary>Who an autopilot destination goes to (ET-525), in this order: the followed character, the only character with the
/// scope, a pick among the characters with the scope, ALLOW… for one character. A followed fleet never takes part: only your own
/// characters carry a token.</summary>
public sealed record MapAutopilotTarget(MapAutopilotPlan Plan, IReadOnlyList<MapAutopilotCandidate> Candidates)
{
    public static MapAutopilotTarget Resolve(MapAutopilotCandidate? followed, IReadOnlyList<MapAutopilotCandidate> own)
    {
        if (followed is not null)
            return new(followed.HasScope ? MapAutopilotPlan.Send : MapAutopilotPlan.Allow, [followed]);

        IReadOnlyList<MapAutopilotCandidate> allowed = _Ordered(own.Where(candidate => candidate.HasScope));
        if (allowed.Count == 1)
            return new(MapAutopilotPlan.Send, allowed);
        if (allowed.Count > 1)
            return new(MapAutopilotPlan.Choose, allowed);

        return own.Count switch
        {
            0 => new(MapAutopilotPlan.NoCharacter, []),
            1 => new(MapAutopilotPlan.Allow, own),
            _ => new(MapAutopilotPlan.ChooseToAllow, _Ordered(own))
        };
    }

    private static List<MapAutopilotCandidate> _Ordered(IEnumerable<MapAutopilotCandidate> candidates) =>
        [.. candidates.OrderBy(candidate => candidate.IsOffline).ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)];
}

public static class MapAutopilotMessages
{
    public static string Failure(EsiError? error, string name) => error?.Kind switch
    {
        EsiErrorKind.ScopeMissing => $"{name} has not allowed setting the destination yet.",
        EsiErrorKind.AuthRequired => $"{name} has to sign in again before the destination can be set.",
        EsiErrorKind.AuthPending => $"{name}'s sign-in is being renewed. Try again in a moment.",
        EsiErrorKind.RateLimited => $"The destination for {name} was not set: ESI is limiting requests ({error.Message}). Try again later.",
        { } => $"The destination could not be set for {name}: {error.Message}",
        _ => $"The destination could not be set for {name}."
    };

    public static bool NeedsAllow(EsiError? error) => error?.Kind is EsiErrorKind.ScopeMissing or EsiErrorKind.AuthRequired;
}
