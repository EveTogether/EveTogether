using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Esi;

/// <summary>
/// The single app-wide gate for ESI-scope-dependent features: given the acting character and the scopes a
/// feature needs, it says whether the feature is allowed and, when not, names the missing scope(s) with a human reason
/// (how to grant them). View-models use it to enable/disable a control + show a tooltip; commands use it to toast a
/// clear message when a scope is missing. Reuses the granted-scopes already on <c>Character</c> — no ESI call.
/// </summary>
public interface IEsiScopeGate
{
    /// <summary>Evaluates the gate for <paramref name="characterId"/> against <paramref name="requiredScopes"/>. An empty
    /// requirement list is always allowed; an unknown character (none granted) is never allowed.</summary>
    Task<ScopeGateState> EvaluateAsync(int characterId, IReadOnlyList<string> requiredScopes, CancellationToken cancellationToken = default);
}
