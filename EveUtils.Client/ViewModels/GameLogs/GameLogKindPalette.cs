using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Client.ViewModels.GameLogs;

/// <summary>One colour per <see cref="GameLogLineKind"/>, used as text and accent on the dark panel (never as a
/// background, so a hovered or selected row keeps its contrast). Every colour clears WCAG AA (4.5:1) on
/// <see cref="Background"/> and no two are alike — both checked by a test.</summary>
public static class GameLogKindPalette
{
    /// <summary>The panel the lines sit on (the theme's <c>BgPanel</c> over <c>BgDeep</c>).</summary>
    public static readonly Color Background = Color.Parse("#FF0C100E");

    public static readonly IReadOnlyDictionary<GameLogLineKind, Color> Colors = new Dictionary<GameLogLineKind, Color>
    {
        [GameLogLineKind.Combat] = Color.Parse("#FFEF5A5A"),
        [GameLogLineKind.Mining] = Color.Parse("#FFE3B341"),
        [GameLogLineKind.Travel] = Color.Parse("#FF38BDF8"),
        [GameLogLineKind.Notify] = Color.Parse("#FFC084FC"),
        [GameLogLineKind.Info] = Color.Parse("#FFB8B2A6"),
        [GameLogLineKind.Hint] = Color.Parse("#FFF472B6"),
        [GameLogLineKind.Bounty] = Color.Parse("#FF4ADE80"),
        [GameLogLineKind.Other] = Color.Parse("#FF8A8F98")
    };

    private static readonly IReadOnlyDictionary<GameLogLineKind, IBrush> Brushes = Build();

    public static IBrush BrushOf(GameLogLineKind kind) => Brushes[kind];

    public static string LabelOf(GameLogLineKind kind) => kind switch
    {
        GameLogLineKind.Combat => "Combat",
        GameLogLineKind.Mining => "Mining",
        GameLogLineKind.Travel => "Travel",
        GameLogLineKind.Notify => "Notify",
        GameLogLineKind.Info => "Info",
        GameLogLineKind.Hint => "Hint",
        GameLogLineKind.Bounty => "Bounty",
        _ => "Other"
    };

    private static Dictionary<GameLogLineKind, IBrush> Build()
    {
        Dictionary<GameLogLineKind, IBrush> brushes = [];
        foreach ((GameLogLineKind kind, Color color) in Colors)
            brushes[kind] = new ImmutableSolidColorBrush(color);

        return brushes;
    }
}
