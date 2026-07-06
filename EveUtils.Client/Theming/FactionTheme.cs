namespace EveUtils.Client.Theming;

/// <summary>
/// The selectable faction colour themes. Each maps to a per-faction <c>ResourceDictionary</c> under
/// <c>Themes/Factions/</c> that ThemeService swaps into the application resources at runtime. An enum (not a set of
/// booleans/strings) keeps the choice type-safe and open for extension. Default = <see cref="Gallente"/>.
/// </summary>
public enum FactionTheme
{
    Gallente,
    Amarr,
    Caldari,
    Minmatar
}
