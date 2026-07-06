using Avalonia.Media;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One CPU / PowerGrid / Calibration arc-gauge on the wheel's outer rim, mirroring the in-game / EVEShipFit
/// fitting ring (validated 2026-06-14). Each gauge is three layers over its angular sweep:
/// a coloured fill arc (used fraction) with a brighter core line, a set of white ticks (brighter where they sit on the
/// fill, dimmer where unused) plus a few larger markers, and an invisible wide hover target carrying the tooltip.
/// The per-resource colour stays the same even over budget; an over-budget gauge is signalled by a pulse on the fill and
/// ticks plus a red readout (the in-game overload cue), not by recolouring the arc red.</summary>
public sealed class RingGaugeViewModel
{
    /// <summary>The used-fraction arc; rendered three times (a blurred glow, the band, the core line).</summary>
    public Geometry FillPath { get; }
    /// <summary>White ticks that fall on the filled portion (drawn brighter).</summary>
    public Geometry TicksOnFill { get; }
    /// <summary>White ticks on the unused portion (drawn dimmer).</summary>
    public Geometry TicksOffFill { get; }
    /// <summary>The few larger marker ticks spread across the sweep.</summary>
    public Geometry Markers { get; }
    /// <summary>A filled, transparent annular sector over the gauge's sweep — a wide hover target for the tooltip, since
    /// the thin ticks are nearly impossible to hover.</summary>
    public Geometry HitArea { get; }
    public IBrush FillColor { get; }
    public IBrush CoreColor { get; }
    public bool IsOverBudget { get; }
    public string Tooltip { get; }

    public RingGaugeViewModel(Geometry fillPath, Geometry ticksOnFill, Geometry ticksOffFill, Geometry markers,
                              Geometry hitArea, IBrush fillColor, IBrush coreColor, bool isOverBudget, string tooltip)
    {
        FillPath = fillPath;
        TicksOnFill = ticksOnFill;
        TicksOffFill = ticksOffFill;
        Markers = markers;
        HitArea = hitArea;
        FillColor = fillColor;
        CoreColor = coreColor;
        IsOverBudget = isOverBudget;
        Tooltip = tooltip;
    }
}
