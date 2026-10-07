using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Enums;

namespace EveUtils.Client.Controls.Killmails;

/// <summary>
/// A killmail's system seen from above (ET-473, mockup v1): X to the right, Z up. <see cref="CelestialMapScale.System"/>
/// draws every celestial with the distance from the sun square-rooted — at true scale the inner planets share one
/// pixel. <see cref="CelestialMapScale.Grid"/> draws the area around the loss at true scale, since on the system scale
/// everything within a few hundred km of it lands on the same point.
/// </summary>
public sealed class CelestialMapControl : Control
{
    private static readonly IImmutableBrush StarBrush = new ImmutableSolidColorBrush(Color.Parse("#F5B042"));
    private static readonly IImmutableBrush StarGlowBrush = new ImmutableSolidColorBrush(Color.Parse("#26F5B042"));
    private static readonly IImmutableBrush PlanetBrush = new ImmutableSolidColorBrush(Color.Parse("#D938BDF8"));
    private static readonly IImmutableBrush MoonBrush = new ImmutableSolidColorBrush(Color.Parse("#6B6457"));
    private static readonly IImmutableBrush StationBrush = new ImmutableSolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IImmutableBrush LossBrush = new ImmutableSolidColorBrush(Color.Parse("#EF5A5A"));
    private static readonly IImmutableBrush FaintLineBrush = new ImmutableSolidColorBrush(Color.Parse("#0DFFFFFF"));
    private static readonly IImmutableBrush RingBrush = new ImmutableSolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IImmutableBrush BrightBrush = new ImmutableSolidColorBrush(Color.Parse("#F3ECE0"));
    private static readonly IImmutableBrush DimBrush = new ImmutableSolidColorBrush(Color.Parse("#8A7E6B"));
    private static readonly IImmutableBrush GateFillBrush = new ImmutableSolidColorBrush(Color.Parse("#144EC79E"));

    private static readonly IPen OrbitPen = new ImmutablePen(FaintLineBrush, 1);
    private static readonly IPen RingPen = new ImmutablePen(RingBrush, 1);
    private static readonly IPen BeltPen = new ImmutablePen(DimBrush, 1, new ImmutableDashStyle([1.5, 1.5], 0));
    private static readonly IPen LossPen = new ImmutablePen(LossBrush, 1.8);
    private static readonly IPen LossRingPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#80EF5A5A")), 1);
    private static readonly IPen StationPen = new ImmutablePen(StationBrush, 1.6);
    private static readonly IPen ScalePen = new ImmutablePen(DimBrush, 1);
    private static readonly IPen NearestLinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#73F3ECE0")), 1,
        new ImmutableDashStyle([3, 3], 0));

    private static readonly Typeface LabelTypeface = Typeface.Default;
    private static readonly Typeface MonoTypeface = new("Consolas");

    public static readonly StyledProperty<KillmailLocation?> LocationProperty =
        AvaloniaProperty.Register<CelestialMapControl, KillmailLocation?>(nameof(Location));

    public static readonly StyledProperty<CelestialMapScale> ScaleProperty =
        AvaloniaProperty.Register<CelestialMapControl, CelestialMapScale>(nameof(Scale));

    static CelestialMapControl()
    {
        AffectsRender<CelestialMapControl>(LocationProperty, ScaleProperty);
    }

    public KillmailLocation? Location
    {
        get => GetValue(LocationProperty);
        set => SetValue(LocationProperty, value);
    }

    public CelestialMapScale Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(MapPalette.BackgroundBrush, bounds);
        if (Location is not { } location || bounds.Width < 40 || bounds.Height < 40)
            return;

        if (Scale == CelestialMapScale.System)
            _RenderSystem(context, bounds, location);
        else
            _RenderGrid(context, bounds, location);
    }

    private static void _RenderSystem(DrawingContext context, Rect bounds, KillmailLocation location)
    {
        var centre = bounds.Center;
        double radius = Math.Min(bounds.Width, bounds.Height) / 2 - 22;
        List<SdeCelestial> celestials = location.ByDistance.Select(entry => entry.Celestial).ToList();
        double furthest = Math.Max(celestials.Max(_DistanceFromSun), Math.Sqrt(location.Loss.X * location.Loss.X + location.Loss.Z * location.Loss.Z));
        if (furthest <= 0)
            return;

        Point ToScreen(double x, double z)
        {
            double scaled = Math.Sqrt(Math.Sqrt(x * x + z * z) / furthest) * radius;
            double angle = Math.Atan2(-z, x);
            return new Point(centre.X + scaled * Math.Cos(angle), centre.Y + scaled * Math.Sin(angle));
        }

        foreach (SdeCelestial planet in celestials.Where(celestial => celestial.Kind == CelestialKind.Planet))
        {
            double orbit = Math.Sqrt(_DistanceFromSun(planet) / furthest) * radius;
            context.DrawEllipse(null, OrbitPen, centre, orbit, orbit);
        }

        context.DrawEllipse(StarGlowBrush, null, centre, 9, 9);
        context.DrawEllipse(StarBrush, null, centre, 4.5, 4.5);

        foreach (SdeCelestial celestial in celestials.Where(celestial => celestial.Kind is CelestialKind.Moon or CelestialKind.AsteroidBelt))
        {
            Point point = ToScreen(celestial.X, celestial.Z);
            if (celestial.Kind == CelestialKind.Moon)
                context.DrawEllipse(MoonBrush, null, point, 1.1, 1.1);
            else
                context.DrawEllipse(null, BeltPen, point, 2.2, 2.2);
        }

        var takenLabels = new List<Rect>();
        foreach (SdeCelestial celestial in celestials.Where(celestial => celestial.Kind is CelestialKind.Planet or CelestialKind.Station or CelestialKind.Stargate))
        {
            Point point = ToScreen(celestial.X, celestial.Z);
            switch (celestial.Kind)
            {
                case CelestialKind.Planet:
                    context.DrawEllipse(PlanetBrush, null, point, 4, 4);
                    _DrawText(context, _PlanetNumeral(celestial.Name), new Point(point.X + 6, point.Y - 6), 9, MapPalette.MutedBrush);
                    break;
                case CelestialKind.Station:
                    context.FillRectangle(StationBrush, new Rect(point.X - 3, point.Y - 3, 6, 6));
                    break;
                default:
                    IImmutableBrush brush = _SecurityBrush(celestial.DestinationSecurity);
                    context.DrawGeometry(null, new ImmutablePen(brush, 1.6), _Diamond(point, 5));
                    FormattedText label = _Text($"{celestial.DestinationName} {_SecurityText(celestial.DestinationSecurity)}", 9.5, brush, LabelTypeface);
                    Point origin = _FreeLabelSpot(takenLabels, point, label);
                    context.DrawText(label, origin);
                    break;
            }
        }

        _DrawLossMarker(context, ToScreen(location.Loss.X, location.Loss.Z), withRing: true);
    }

    private static void _RenderGrid(DrawingContext context, Rect bounds, KillmailLocation location)
    {
        Point centre = bounds.Center;
        double half = location.GridHalfExtentMetres;
        double scale = (Math.Min(bounds.Width, bounds.Height) / 2 - 22) / half;
        Point ToScreen(SdeCelestial celestial) =>
            new(centre.X + (celestial.X - location.Loss.X) * scale, centre.Y - (celestial.Z - location.Loss.Z) * scale);

        for (var ring = 1; ring <= 4; ring++)
        {
            double size = half * ring / 4 * scale;
            context.DrawEllipse(null, ring == 4 ? RingPen : OrbitPen, centre, size, size);
        }

        FormattedText ringLabel = _Text(SpaceDistance.Text(half), 9, DimBrush, LabelTypeface);
        context.DrawText(ringLabel, new Point(centre.X + half * scale - ringLabel.Width - 3, centre.Y - ringLabel.Height - 2));

        foreach (var (celestial, metres) in location.ByDistance.TakeWhile(entry => entry.Metres <= half * 1.05))
        {
            Point point = ToScreen(celestial);
            switch (celestial.Kind)
            {
                case CelestialKind.Stargate:
                    context.DrawGeometry(GateFillBrush, new ImmutablePen(_SecurityBrush(celestial.DestinationSecurity), 1.8), _Diamond(point, 9));
                    break;
                case CelestialKind.AsteroidBelt:
                    context.DrawEllipse(null, BeltPen, point, 8, 8);
                    break;
                case CelestialKind.Station:
                    context.DrawRectangle(null, StationPen, new Rect(point.X - 6, point.Y - 6, 12, 12));
                    break;
                default:
                    context.DrawEllipse(PlanetBrush, null, point, 7, 7);
                    break;
            }

            FormattedText name = _Text(celestial.Name, 10, MapPalette.TextBrush, LabelTypeface);
            context.DrawText(name, new Point(point.X - name.Width / 2, point.Y + 12));
        }

        if (location.NearestMetres <= half * 1.05)
        {
            Point nearest = ToScreen(location.Nearest);
            context.DrawLine(NearestLinePen, centre, nearest);
            FormattedText distance = _Text(SpaceDistance.Text(location.NearestMetres), 10.5, BrightBrush, MonoTypeface);
            context.DrawText(distance, _DistanceLabelSpot(centre, nearest, distance));
        }
        else
        {
            FormattedText none = _Text("no celestial within grid range", 10.5, MapPalette.MutedBrush, LabelTypeface);
            context.DrawText(none, new Point(centre.X - none.Width / 2, centre.Y + 30));
            FormattedText nearestLine = _Text($"nearest: {location.Nearest.Name}, {SpaceDistance.Text(location.NearestMetres)}", 10.5,
                MapPalette.MutedBrush, LabelTypeface);
            context.DrawText(nearestLine, new Point(centre.X - nearestLine.Width / 2, centre.Y + 30 + none.Height + 2));
        }

        _DrawLossMarker(context, centre, withRing: false);

        double bar = half / 2 * scale;
        double barY = bounds.Bottom - 14;
        context.DrawLine(ScalePen, new Point(12, barY), new Point(12 + bar, barY));
        context.DrawLine(ScalePen, new Point(12, barY - 4), new Point(12, barY + 4));
        context.DrawLine(ScalePen, new Point(12 + bar, barY - 4), new Point(12 + bar, barY + 4));
        FormattedText barLabel = _Text(SpaceDistance.Text(half / 2), 9, DimBrush, LabelTypeface);
        context.DrawText(barLabel, new Point(12 + bar / 2 - barLabel.Width / 2, barY - 6 - barLabel.Height));

        // Seen from above, the height difference is invisible, so it is written out.
        double height = Math.Abs(location.Nearest.Y - location.Loss.Y);
        FormattedText heightLabel = _Text($"top-down · height Δ {SpaceDistance.Text(height)}", 9, DimBrush, LabelTypeface);
        context.DrawText(heightLabel, new Point(bounds.Right - heightLabel.Width - 8, barY - heightLabel.Height / 2));
    }

    // Behind the loss marker, on the far side from the object, whose own name sits under it. When the two almost
    // coincide from above — a gate 6 km away but mostly above or below — "behind" is no direction, so it goes over both.
    private static Point _DistanceLabelSpot(Point loss, Point target, FormattedText label)
    {
        double dx = target.X - loss.X;
        double dy = target.Y - loss.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 40)
            return new Point((loss.X + target.X) / 2 - label.Width / 2, Math.Min(loss.Y, target.Y) - 14 - label.Height);

        double clearance = 14 + Math.Max(label.Width, label.Height) / 2;
        var centre = new Point(loss.X - dx / length * clearance, loss.Y - dy / length * clearance);
        return new Point(centre.X - label.Width / 2, centre.Y - label.Height / 2);
    }

    private static void _DrawLossMarker(DrawingContext context, Point point, bool withRing)
    {
        if (withRing)
            context.DrawEllipse(null, LossRingPen, point, 11, 11);
        context.DrawLine(LossPen, new Point(point.X - 8, point.Y), new Point(point.X + 8, point.Y));
        context.DrawLine(LossPen, new Point(point.X, point.Y - 8), new Point(point.X, point.Y + 8));
        context.DrawEllipse(LossBrush, null, point, 2.6, 2.6);
    }

    // Above the gate, or below / further below when another gate label already sits there: two gates of the same
    // planet otherwise print on top of each other.
    private static Point _FreeLabelSpot(List<Rect> taken, Point anchor, FormattedText label)
    {
        double[] offsets = [-8 - label.Height, 8, 8 + label.Height, -8 - 2 * label.Height];
        foreach (double offset in offsets)
        {
            var spot = new Rect(anchor.X - label.Width / 2, anchor.Y + offset, label.Width, label.Height);
            if (taken.All(other => !other.Intersects(spot)))
            {
                taken.Add(spot);
                return spot.Position;
            }
        }
        return new Point(anchor.X - label.Width / 2, anchor.Y + offsets[0]);
    }

    private static double _DistanceFromSun(SdeCelestial celestial) =>
        Math.Sqrt(celestial.X * celestial.X + celestial.Z * celestial.Z);

    // "Arnher VIII" → "VIII"; the system name is already on the panel.
    private static string _PlanetNumeral(string planetName) =>
        planetName[(planetName.LastIndexOf(' ') + 1)..];

    // The game shows anything above 0.0 that rounds down to it as 0.1, so a lowsec gate never reads as nullsec.
    private static double _DisplaySecurity(double security) => security is > 0 and < 0.05 ? 0.1 : security;

    private static IImmutableBrush _SecurityBrush(double? security) =>
        MapPalette.SecurityBrush(_DisplaySecurity(security ?? 0));

    private static string _SecurityText(double? security) =>
        Math.Round(_DisplaySecurity(security ?? 0), 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

    private static Geometry _Diamond(Point centre, double size)
    {
        var geometry = new StreamGeometry();
        using var draw = geometry.Open();
        draw.BeginFigure(new Point(centre.X, centre.Y - size), true);
        draw.LineTo(new Point(centre.X + size, centre.Y));
        draw.LineTo(new Point(centre.X, centre.Y + size));
        draw.LineTo(new Point(centre.X - size, centre.Y));
        draw.EndFigure(true);
        return geometry;
    }

    private static void _DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush) =>
        context.DrawText(_Text(text, size, brush, LabelTypeface), origin);

    private static FormattedText _Text(string text, double size, IBrush brush, Typeface typeface) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
