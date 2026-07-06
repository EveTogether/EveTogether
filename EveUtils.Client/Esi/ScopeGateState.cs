using System.Collections.Generic;

namespace EveUtils.Client.Esi;

/// <summary>
/// The result of an ESI-scope gate check: whether a feature is allowed for a character, and when it is not,
/// which scope(s) are missing plus a human <see cref="Reason"/> (which access + how to grant it) for a disabled-control
/// tooltip or a toast.
/// </summary>
public sealed record ScopeGateState(bool IsAllowed, IReadOnlyList<string> MissingScopes, string? Reason)
{
    /// <summary>The feature has every scope it needs (or needs none).</summary>
    public static readonly ScopeGateState Allowed = new(true, [], null);
}
