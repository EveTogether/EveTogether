using Avalonia.Media;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One slot box positioned on the radial fitting wheel: its canvas position, a category
/// glyph/colour and the module name as tooltip. Empty-icon boxes mirror the in-game fitting ring.</summary>
public sealed class FitRadialSlotViewModel
{
    /// <summary>The curved annular-segment tile for an empty slot (same shape as a filled slot, dimmed), so the ring
    /// reads as a continuous band of slots like the in-game wheel. Parsed lazily so
    /// constructing the view-model needs no render backend (tests).</summary>
    private readonly string _shapePath;
    private Geometry? _shape;
    public Geometry Shape => _shape ??= Geometry.Parse(_shapePath);
    public string Glyph { get; }
    public IBrush Color { get; }
    public string Tooltip { get; }

    public FitRadialSlotViewModel(string shape, string glyph, string colorHex, string tooltip)
    {
        _shapePath = shape;
        Glyph = glyph;
        Color = new SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
        Tooltip = tooltip;
    }
}
