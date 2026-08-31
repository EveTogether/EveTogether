using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.Esi;

/// <summary>
/// Puts a character's granted ESI scopes into words, for the tooltip on the scopes block of the character dialog.
/// </summary>
/// <remarks>
/// It describes what was actually granted, never what the app would like to have — where those two differ is
/// precisely what the reader is looking for.
/// </remarks>
public static class EsiScopeSummary
{
    /// <summary>
    /// One line per granted scope, named after the feature that declared it, or the raw scope where nothing did.
    /// </summary>
    /// <param name="granted">The scopes EVE actually granted; empty or null is a state of its own, not a blank.</param>
    /// <param name="known">The scopes this build declares, for turning a scope string into something readable.</param>
    public static string Describe(IReadOnlyList<string>? granted, IReadOnlyList<EsiScopeRequirement> known)
    {
        if (granted is null || granted.Count == 0)
            return "No ESI scopes granted. Re-authenticate to choose what this character shares.";

        var byScope = known
            .GroupBy(requirement => requirement.Scope, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Feature, System.StringComparer.OrdinalIgnoreCase);

        // The raw scope for anything this build does not declare: a grant can outlive the feature that asked for it,
        // and a half-translated list would hide exactly that.
        var lines = granted
            .Select(scope => byScope.TryGetValue(scope, out var feature) ? $"{feature} ({scope})" : scope)
            .OrderBy(line => line, System.StringComparer.OrdinalIgnoreCase);

        return "Currently shared:\n" + string.Join("\n", lines);
    }
}
