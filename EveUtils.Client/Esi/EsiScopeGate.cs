using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.Esi;

/// <summary>
/// <see cref="IEsiScopeGate"/> over the character registry (granted scopes) + the scope registry (feature
/// metadata). A feature is allowed when the acting character has granted every required scope; otherwise the
/// reason names the human feature of each missing scope and how to grant it (re-sign-in). Singleton.
/// </summary>
public sealed class EsiScopeGate(ICharacterRegistry characters, IEsiScopeRegistry scopes) : IEsiScopeGate, ISingletonService
{
    public async Task<ScopeGateState> EvaluateAsync(int characterId, IReadOnlyList<string> requiredScopes, CancellationToken cancellationToken = default)
    {
        if (requiredScopes.Count == 0)
            return ScopeGateState.Allowed;

        var character = (await characters.GetAllAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.EsiCharacterId == characterId);

        var missing = requiredScopes
            .Where(scope => character is null || !character.HasScope(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return missing.Count == 0
            ? ScopeGateState.Allowed
            : new ScopeGateState(false, missing, BuildReason(missing, character));
    }

    private string BuildReason(IReadOnlyList<string> missing, Character? character)
    {
        var requirements = scopes.GetRequirements(EsiScopeTarget.Client);
        var features = missing
            .Select(scope => requirements.FirstOrDefault(r => string.Equals(r.Scope, scope, StringComparison.OrdinalIgnoreCase))?.Feature)
            .Where(feature => !string.IsNullOrEmpty(feature))
            .Distinct()
            .ToList();

        var who = character?.Name is { Length: > 0 } name ? name : "this character";
        var access = features.Count switch
        {
            0 => "ESI access",
            1 => $"the '{features[0]}' ESI access",
            _ => $"these ESI accesses: {string.Join(", ", features)}"
        };
        var pronoun = missing.Count == 1 ? "it" : "them";
        return $"Missing {access}. Re-sign in {who} and grant {pronoun} (scope: {string.Join(", ", missing)}) to enable this.";
    }
}
