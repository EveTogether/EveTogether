using System;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Shared with every list built from <see cref="CharacterPickRowViewModel"/> rows (ET-184's own
/// <see cref="EveUtils.Client.Views.CharacterPickerWindow"/> and the SKILLS header character choice, ET-16): a
/// search field only earns its place once scrolling a plain list stops being the faster way to find a name.
/// </summary>
public static class CharacterPickerSearch
{
    /// <summary>Below this many rows, scanning the list by eye is faster than typing — the search field stays
    /// hidden (ET-341 Round 3 / ET-16 AC1).</summary>
    public const int SearchThreshold = 9;

    /// <summary>Case-insensitive substring match on the row's name; an empty query matches everything.</summary>
    public static bool Matches(CharacterPickRowViewModel row, string? query) =>
        string.IsNullOrWhiteSpace(query) || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
}
