using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Client.Controls.Map;

/// <summary>
/// The map's own colours (ET-392, mockup v2). They are not themed: region colours only tell neighbours apart and
/// security colours are the game's scale, lifted until each reaches 4.6:1 on the map, panel and HUD backgrounds of
/// all four factions. Overlays that belong to the app — markers, selection, hover — use the faction's AccentBright.
/// </summary>
public static class MapPalette
{
    public static readonly Color Background = Color.Parse("#06070A");
    public static readonly Color HudBackground = Color.Parse("#EB0C100E");
    public static readonly Color Text = Color.Parse("#D8D0C4");
    public static readonly Color Muted = Color.Parse("#A0958A");
    public static readonly Color CrossRegionLine = Color.Parse("#8B95A5");
    public static readonly Color Route = Color.Parse("#FFE066");

    /// <summary>Index = displayed security × 10; 0 also covers everything at or below 0.0.</summary>
    public static readonly IReadOnlyList<Color> Security =
    [
        Color.Parse("#AE6993"), Color.Parse("#A2726E"), Color.Parse("#CF5458"), Color.Parse("#D35727"),
        Color.Parse("#DC6D07"), Color.Parse("#F3FD82"), Color.Parse("#71E554"), Color.Parse("#60DBA3"),
        Color.Parse("#4ECEF8"), Color.Parse("#3A9AEB"), Color.Parse("#397CE1")
    ];

    /// <summary>One per <see cref="MapRegionDto.ColourIndex"/>.</summary>
    public static readonly IReadOnlyList<Color> Regions =
    [
        Color.Parse("#FF9D4D"), Color.Parse("#7FD36B"), Color.Parse("#56C8E0"), Color.Parse("#8FB0FF"),
        Color.Parse("#C3A3FF"), Color.Parse("#FF85BD"), Color.Parse("#FF7A66"), Color.Parse("#E3DCCB"),
        Color.Parse("#8EE0C3"), Color.Parse("#D9B98A")
    ];

    public static IImmutableBrush BackgroundBrush { get; } = new ImmutableSolidColorBrush(Background);
    public static IImmutableBrush HudBackgroundBrush { get; } = new ImmutableSolidColorBrush(HudBackground);
    public static IImmutableBrush TextBrush { get; } = new ImmutableSolidColorBrush(Text);
    public static IImmutableBrush MutedBrush { get; } = new ImmutableSolidColorBrush(Muted);
    public static IImmutableBrush CrossRegionLineBrush { get; } = new ImmutableSolidColorBrush(CrossRegionLine);
    public static IImmutableBrush RouteBrush { get; } = new ImmutableSolidColorBrush(Route);
    public static IImmutableBrush SampleRegionBrush { get; } = new ImmutableSolidColorBrush(Regions[2]);
    public static IImmutableBrush HighsecBrush { get; } = new ImmutableSolidColorBrush(Security[10]);
    public static IImmutableBrush LowsecBrush { get; } = new ImmutableSolidColorBrush(Security[3]);
    public static IImmutableBrush NullsecBrush { get; } = new ImmutableSolidColorBrush(Security[0]);

    private static readonly IImmutableBrush[] SecurityBrushes = [.. Security.Select(colour => new ImmutableSolidColorBrush(colour))];

    /// <summary>The legend's security scale, 1.0 down to 0.0.</summary>
    public static IReadOnlyList<MapSecurityStep> SecurityScale { get; } =
        [.. Enumerable.Range(0, 11).Reverse().Select(step => new MapSecurityStep((step / 10.0).ToString("0.0", CultureInfo.InvariantCulture), SecurityBrushes[step]))];

    public static Color SecurityColour(double displaySecurity) => Security[_SecurityStep(displaySecurity)];

    public static IImmutableBrush SecurityBrush(double displaySecurity) => SecurityBrushes[_SecurityStep(displaySecurity)];

    private static int _SecurityStep(double displaySecurity) =>
        displaySecurity <= 0 ? 0 : Math.Clamp((int)Math.Round(displaySecurity * 10, MidpointRounding.AwayFromZero), 0, 10);
}
