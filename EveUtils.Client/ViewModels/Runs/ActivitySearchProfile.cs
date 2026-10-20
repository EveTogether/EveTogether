using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// What the runs search (ET-518) can find an activity by, unmarked: the typed term must match what the site is called,
/// never what OPSEC masks it as. Built once per activity when its row is built, so a keystroke never reads the static data.
/// </summary>
/// <param name="Name">The site's name as recorded, or what the static data calls it.</param>
/// <param name="Faction">The static data's faction; null where the site is not catalogued.</param>
/// <param name="Archetype">The static data's site family; null where the site is not catalogued.</param>
/// <param name="Extra">Anything else worth finding it by: the DED rating, a mission's level.</param>
public sealed record ActivitySearchProfile(string Name, string? Faction, string? Archetype, string TypeName, string Extra = "")
{
    /// <summary>Name, faction, family, type and the rest, in one string the words of a search are matched against.</summary>
    public string Text { get; } = string.Join(' ', new[] { Name, Faction, Archetype, TypeName, Extra }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public static ActivitySearchProfile Empty { get; } = new(string.Empty, null, null, string.Empty);
}

/// <summary>The words of a search and what they match: every word must start a word of the text, in any order.</summary>
public static class ActivitySearchTerms
{
    private static readonly char[] Separators = [' ', '\t', '·', '-', '/', ',', '(', ')', '\'', '’'];

    public static IReadOnlyList<string> Words(string? term) =>
        string.IsNullOrWhiteSpace(term) ? [] : [.. term.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static bool Matches(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return true;

        string[] textWords = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return words.All(word => textWords.Any(candidate => candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase)));
    }
}
