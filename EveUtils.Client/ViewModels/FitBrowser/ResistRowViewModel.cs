using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One layer row in the resistance panel: the layer name, its four resist percentages as
/// in-game-style proportional fill bars (the coloured bar fills left-to-right to the resist %, tinted by damage type —
/// EM blue, thermal red, kinetic grey, explosive orange — with the number overlaid) and its EHP.</summary>
public sealed class ResistRowViewModel
{
    // Damage-type fill colours (the in-game resist coding); the bar fills to the resist %, number overlaid on top.
    private static readonly IBrush EmBarBrush = Brush.Parse("#B34E8AD9");
    private static readonly IBrush ThermalBarBrush = Brush.Parse("#B3D9544E");
    private static readonly IBrush KineticBarBrush = Brush.Parse("#B39AA7B0");
    private static readonly IBrush ExplosiveBarBrush = Brush.Parse("#B3D9A441");

    public string Layer { get; }
    public string Em { get; }
    public string Thermal { get; }
    public string Kinetic { get; }
    public string Explosive { get; }
    public string Ehp { get; }

    // Per cell a filled/remaining star pair so a two-column Grid draws the bar proportional to the resist %.
    public GridLength EmFill { get; }
    public GridLength EmRest { get; }
    public GridLength ThermalFill { get; }
    public GridLength ThermalRest { get; }
    public GridLength KineticFill { get; }
    public GridLength KineticRest { get; }
    public GridLength ExplosiveFill { get; }
    public GridLength ExplosiveRest { get; }

    public IBrush EmBar => EmBarBrush;
    public IBrush ThermalBar => ThermalBarBrush;
    public IBrush KineticBar => KineticBarBrush;
    public IBrush ExplosiveBar => ExplosiveBarBrush;

    public ResistRowViewModel(string layer, ResistLayer resists, double ehp)
    {
        Layer = layer;
        Em = $"{resists.Em:0}%";
        Thermal = $"{resists.Thermal:0}%";
        Kinetic = $"{resists.Kinetic:0}%";
        Explosive = $"{resists.Explosive:0}%";
        Ehp = $"{ehp:N0}";
        (EmFill, EmRest) = Bar(resists.Em);
        (ThermalFill, ThermalRest) = Bar(resists.Thermal);
        (KineticFill, KineticRest) = Bar(resists.Kinetic);
        (ExplosiveFill, ExplosiveRest) = Bar(resists.Explosive);
    }

    private static (GridLength fill, GridLength rest) Bar(double pct)
    {
        var clamped = System.Math.Clamp(pct, 0, 100);
        return (new GridLength(clamped, GridUnitType.Star), new GridLength(100 - clamped, GridUnitType.Star));
    }
}
