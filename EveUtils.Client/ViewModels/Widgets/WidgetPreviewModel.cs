using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using EveUtils.Client.Controls;
using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>
/// What <c>widget-page.html</c> draws for a config, on fixed sample figures, rebuilt on every change. It mirrors the page:
/// each preset lists the same figures, rows and notes for the same field keys, the together and minimal themes draw them
/// as a panel and the ticker as one bar. A change to a preset's layout on the page belongs here too.
/// </summary>
public sealed class WidgetPreviewModel
{
    private const double FigureSize = 15, HeadlineSize = 30, MidSize = 18, ClockSize = 26;
    private static readonly Color PanelColor = Color.FromRgb(12, 16, 14);
    private static readonly IBrush Bright = new SolidColorBrush(Color.Parse("#F3ECE0"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#A0958A"));
    private static readonly IBrush Positive = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush Negative = new SolidColorBrush(Color.Parse("#EF5A5A"));
    private static readonly IBrush Track = new SolidColorBrush(Colors.White, 0.1);
    private static readonly IBrush Baseline = new SolidColorBrush(Colors.White, 0.13);

    private readonly IReadOnlySet<string> _fields;
    private readonly WidgetConfig _config;
    private readonly List<object> _items = [];
    private List<WidgetPreviewFigure>? _stats;

    private WidgetPreviewModel(WidgetConfig config)
    {
        _config = config;
        _fields = config.Fields.ToHashSet();
        var accent = Color.TryParse(config.Accent, out var parsed) ? parsed : Color.Parse(WidgetPresets.DefaultAccent);
        Accent = new SolidColorBrush(accent);
        AccentLight = new SolidColorBrush(_Lighten(accent));
        Width = WidgetPresets.Size(config).Width;
        Scale = config.Scale / 100.0;
        IsTicker = config.Theme is WidgetTheme.Ticker;
        IsTogether = config.Theme is WidgetTheme.Together;
        Background = config.Background is WidgetBackground.Panel
            ? new SolidColorBrush(PanelColor, config.PanelOpacity / 100.0)
            : Brushes.Transparent;
        Frame = IsTogether ? new SolidColorBrush(accent, 0.55) : Accent;
        FrameThickness = config.Theme switch
        {
            WidgetTheme.Together => new Thickness(1),
            WidgetTheme.Ticker => new Thickness(0, 2, 0, 0),
            _ => new Thickness(0)
        };
        HeaderBackground = IsTogether
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Color.FromArgb(46, accent.R, accent.G, accent.B), 0), new GradientStop(Colors.Transparent, 1)]
            }
            : Brushes.Transparent;
        HeaderRule = new SolidColorBrush(accent, IsTogether ? 0.35 : 0);
        HeaderForeground = IsTogether ? Bright : AccentLight;

        (Title, Right) = config.Preset switch
        {
            WidgetPreset.LiveDps => _LiveDps(),
            WidgetPreset.DpsGraph => _DpsGraph(),
            WidgetPreset.CurrentRun => _CurrentRun(),
            WidgetPreset.RunTotals => _RunTotals(),
            WidgetPreset.AbyssalTotals => _AbyssalTotals(),
            WidgetPreset.LastKillmail => _LastKillmail(),
            WidgetPreset.KillAlert => _KillAlert(),
            WidgetPreset.FleetDps => _FleetDps(),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Preset, null)
        };
        Items = _items;
        TickerTitle = Right.Length > 0 ? $"{Title} · {Right}" : Title;
        TickerItems = [.. _items.SelectMany(_TickerFigures)];
    }

    public string Title { get; }
    public string Right { get; }
    public string TickerTitle { get; }
    public string? Icon { get; private set; }
    public bool HasIcon => Icon is not null;
    public IReadOnlyList<object> Items { get; }
    public IReadOnlyList<WidgetPreviewFigure> TickerItems { get; }
    public IBrush Accent { get; }
    public IBrush AccentLight { get; }
    public IBrush Background { get; }
    public IBrush Frame { get; }
    public Thickness FrameThickness { get; }
    public IBrush HeaderBackground { get; }
    public IBrush HeaderRule { get; }
    public IBrush HeaderForeground { get; }
    public bool IsTicker { get; }
    public bool IsPanel => !IsTicker;
    public bool IsTogether { get; }
    public double Width { get; }
    public double Scale { get; }

    public static WidgetPreviewModel From(WidgetConfig config) => new(config);

    private (string, string) _LiveDps()
    {
        if (_Has("dpsOut")) _Figure("DPS out", "765", Bright, HeadlineSize);
        if (_Has("dpsIn")) _Figure("DPS in", "201", Negative, MidSize);
        if (_Has("peak")) _Stat("Peak", "812");
        if (_Has("reps"))
        {
            _Stat("Reps in", "187", Positive);
            _Stat("Reps out", "0", Positive);
        }
        if (_Has("bounty")) _Stat("Bounty", "6.2M", AccentLight);
        if (_Has("application")) _Stat("Applied", "Sweet spot", Positive);
        if (_Located("system")) _Note("Otela");
        return (_Has("name") ? "KAELEN VOSS" : "LIVE DPS", "LIVE");
    }

    private (string, string) _DpsGraph()
    {
        var capLane = _Has("neut") || _Has("cap");
        double width = Width - 20, height = capLane ? 130 : 120, hitPointsHeight = capLane ? 86 : height, capTop = hitPointsHeight + 8;
        List<WidgetPreviewLine> lines =
        [
            _Straight(hitPointsHeight - 0.5, width),
            .. capLane ? [_Straight(height - 0.5, width)] : Array.Empty<WidgetPreviewLine>()
        ];
        List<WidgetPreviewNote> legend = [];

        void Line(string field, string label, Color ink, double figure, Func<double, double> rate, bool capacitor, bool dashed = false)
        {
            if (!_Has(field)) return;
            double top = capacitor ? capTop : 0, laneHeight = capacitor ? height - capTop : hitPointsHeight, max = capacitor ? 50 : 1000;
            var points = Enumerable.Range(0, 121)
                .Select(i => new Point(i / 120.0 * width, top + laneHeight - Math.Min(rate(i), max) / max * (laneHeight - 2) - 1))
                .ToList();
            var brush = new SolidColorBrush(ink);
            lines.Add(new WidgetPreviewLine(points, brush, 1.6, dashed ? [4, 3] : null));
            if (figure > 0 || field is "dpsOut" or "dpsIn") legend.Add(new WidgetPreviewNote($"{label} {figure:N0}", brush));
        }

        Line("dpsOut", "OUT", CombatInk.OutColor, 584, i => 560 + 140 * Math.Sin(i / 9.0) + 40 * Math.Sin(i * 1.3) + (i > 100 && i < 112 ? 180 : 0), capacitor: false);
        Line("dpsIn", "IN", CombatInk.InColor, 208, i => 200 + 90 * Math.Max(0, Math.Sin(i / 11.0 + 2)) + 15 * Math.Sin(i * 1.7), capacitor: false);
        Line("reps", "REP IN", CombatInk.RepColor, 173, i => 150 + 40 * Math.Sin(i / 7.0), capacitor: false);
        Line("neut", "NEUT IN", CombatInk.NeutColor, 26, i => i % 50 < 18 ? 24 + 3 * Math.Sin(i) : 0, capacitor: true);
        Line("cap", "CAP IN", CombatInk.CapColor, 8, i => 8 + 2 * Math.Sin(i / 3.0), capacitor: true);

        _Node(new WidgetPreviewGraph(width, height, lines, "HP/s 1,000", capLane ? "GJ/s 50" : null, capTop,
            _Has("numbers") ? legend : []));
        if (_Located("system")) _Note("Otela");
        var span = _config.Options.GetValueOrDefault("span") switch { "120" => "2 min", "300" => "5 min", _ => "60 s" };
        return (_Has("name") ? "KAELEN VOSS" : "DPS", span);
    }

    private (string, string) _CurrentRun()
    {
        var headline = string.Join(" · ", new[] { _Has("type") ? "Abyssal" : null, _Has("site") ? "Abyssal Deadspace" : null }.OfType<string>());
        if (headline.Length > 0 || _Has("tier")) _Row(headline, _Has("tier") ? "T5 · Electrical" : "", Bright, AccentLight);
        _Note(string.Join(" · ", new[] { _Located("signature") ? "RUS-326" : null, _Located("system") ? "Otela" : null }.OfType<string>()));
        if (_Has("clock"))
        {
            _Figure("Time left", "11:30", Bright, ClockSize);
            _Fill(11.5 / 20);
        }
        if (_Has("loot")) _Stat("Loot", "38.4M", AccentLight);
        if (_Has("bounty")) _Stat("Bounty", "0");
        if (_Has("kills")) _Stat("Kills", "23");
        if (_Has("total")) _Figure("Run total", "38.4M ISK", AccentLight, MidSize);
        if (_Has("crew")) _Note("Crew: Kaelen Voss · Mira Tanaka-Holt");
        return ("CURRENT RUN", "2 PILOTS");
    }

    private (string, string) _RunTotals()
    {
        if (_Has("isk")) _Figure("Total", "248.6M", AccentLight, HeadlineSize);
        if (_Has("runs")) _Stat("Runs", "7");
        if (_Has("iskPerHour")) _Stat("ISK / hour", "92.1M", Positive);
        if (_Has("flownTime")) _Stat("Flown", "2:42:10");
        if (_Has("averagePerRun")) _Stat("Avg / run", "35.5M");
        if (_Has("bestDrop")) _Node(new WidgetPreviewRow("BEST DROP", "Gravid Gyrostabilizer Mutaplasmid · 41.2M", Dim, Bright, LeftIsLabel: true));
        if (_Has("perCharacter"))
        {
            _Row("Kaelen Voss", "181.0M", Bright, Bright);
            _Row("Mira Tanaka-Holt", "67.6M", Bright, Bright);
        }
        var kind = _config.Options.GetValueOrDefault("kind", "all");
        return ("RUN TOTALS", string.Join(" · ", new[] { _Period(), kind == "all" ? null : kind }.OfType<string>()).ToUpperInvariant());
    }

    private (string, string) _AbyssalTotals()
    {
        if (_Has("isk")) _Figure("Abyssal ISK", "231.4M", AccentLight, HeadlineSize);
        if (_Has("runs")) _Stat("Clears", "6");
        if (_Has("iskPerHour")) _Stat("ISK / hour", "118.7M", Positive);
        if (_Has("averageClear")) _Stat("Avg clear", "14:52");
        if (_Has("shipsLost")) _Stat("Ships lost", "0", Positive);
        if (_Has("byTier"))
        {
            (string Name, string Runs, double Isk, string Ink)[] tiers =
                [("T5 Electrical", "3×", 141.2, "#8FC6F0"), ("T5 Dark", "2×", 64.9, "#9085E9"), ("T4 Firestorm", "1×", 25.3, "#EF5A5A")];
            double room = Width - 20 - 4 * (tiers.Length - 1), total = tiers.Sum(tier => tier.Isk);
            _Node(new WidgetPreviewBar(
                [.. tiers.Select(tier => new WidgetPreviewSegment(room * tier.Isk / total, new SolidColorBrush(Color.Parse(tier.Ink))))], 6, 4));
            foreach (var tier in tiers) _Row("■ " + tier.Name, $"{tier.Runs}  {tier.Isk:0.0}M", new SolidColorBrush(Color.Parse(tier.Ink)), Bright);
        }
        return ("ABYSSAL", _Period().ToUpperInvariant());
    }

    private (string, string) _LastKillmail()
    {
        var loss = _config.Options.GetValueOrDefault("show") == "losses";
        if (_Has("ship")) Icon = loss ? "G" : "VNI";
        if (_Has("badge") || _Has("ago"))
            _Row("", _Has("ago") ? "3 min ago" : "", Bright, Dim, badge: _Has("badge") ? loss ? "LOSS" : "KILL" : null, loss: loss);
        var victim = _Has("victim") ? (loss ? "Kaelen Voss" : "Rask Velloran") + (_Has("corporation") ? " [VLRN]" : "") : null;
        var who = string.Join(" · ", new[] { _Has("ship") ? loss ? "Gila" : "Vexor Navy Issue" : null, victim }.OfType<string>());
        if (who.Length > 0) _Row(who, "", Bright, Bright);
        var detail = string.Join(" · ", new[] { _Has("attackers") ? "4 attackers" : null, _Has("finalBlow") ? "FB Kaelen Voss" : null }.OfType<string>());
        if (_Has("isk")) _items.Add(new WidgetPreviewRow(loss ? "512.3M" : "142.8M", detail, loss ? Negative : Positive, Dim, LeftFontSize: MidSize));
        else _Note(detail);
        _Note(string.Join(" · ", new[] { _Has("run") ? "During a run" : null, _Located("system") ? "Otela" : null }.OfType<string>()));
        return ("LAST KILLMAIL", "");
    }

    private (string, string) _KillAlert()
    {
        if (_Has("ship")) Icon = "VNI";
        _items.Add(new WidgetPreviewRow("", _Has("isk") ? "142.8M" : "", Bright, Positive, RightFontSize: MidSize, Badge: "KILL", BadgeBrush: Positive));
        if (_Has("ship")) _Row("Vexor Navy Issue", "", Bright, Bright);
        _Note(string.Join(" · ", new[] { _Has("victim") ? "Rask Velloran" : null, _Located("system") ? "Otela" : null }.OfType<string>()));
        return ("NEW KILL", _config.Options.GetValueOrDefault("holdSeconds", "8") + " S");
    }

    private (string, string) _FleetDps()
    {
        (string Name, double Out, double In, string System)[] members =
            [("Kaelen Voss", 612, 140, "J-RQMF"), ("Mira Tanaka-Holt", 488, 40, "J-RQMF"), ("Pilot 3", 0, 12, "Otela")];
        for (var i = 0; i < members.Length; i++)
        {
            var member = members[i];
            var name = (_Has("names") ? member.Name : $"Member {i + 1}") + (_Located("systems") ? "  " + member.System : "");
            var figures = string.Join("  ", new[] { _Has("dpsOut") ? $"{member.Out:N0}" : null, _Has("dpsIn") ? $"{member.In:N0}" : null }.OfType<string>());
            _Row(name, figures, Bright, Bright);
            if (_Has("dpsOut")) _Fill(Math.Min(1, member.Out / 1000), Bright);
        }
        if (_Has("total")) _Figure("Fleet DPS", "1,100", AccentLight, MidSize);
        return ("FLEET", $"{members.Length} MEMBERS");
    }

    private bool _Has(string field) => _fields.Contains(field);

    private bool _Located(string field) => _config.ShowLocation && _Has(field);

    private string _Period() => _config.Options.GetValueOrDefault("period", "session");

    private void _Figure(string label, string value, IBrush brush, double size) => _Node(new WidgetPreviewFigure(label.ToUpperInvariant(), value, brush, size));

    // Stats in a row share one grid, as on the page; anything else in between starts a new one.
    private void _Stat(string label, string value, IBrush? brush = null)
    {
        if (_stats is null)
        {
            _stats = [];
            _items.Add(new WidgetPreviewStats(_stats));
        }
        _stats.Add(new WidgetPreviewFigure(label.ToUpperInvariant(), value, brush ?? Bright, FigureSize));
    }

    private void _Row(string left, string right, IBrush leftBrush, IBrush rightBrush, string? badge = null, bool loss = false) =>
        _Node(new WidgetPreviewRow(left, right, leftBrush, rightBrush, Badge: badge, BadgeBrush: loss ? Negative : Positive));

    private void _Note(string text)
    {
        if (text.Length > 0) _Node(new WidgetPreviewNote(text, Dim));
    }

    private void _Fill(double fraction, IBrush? brush = null)
    {
        var width = Width - 20;
        _Node(new WidgetPreviewBar([new WidgetPreviewSegment(width * fraction, brush ?? Accent), new WidgetPreviewSegment(width * (1 - fraction), Track)], 4, 0));
    }

    private void _Node(object item)
    {
        _stats = null;
        _items.Add(item);
    }

    // The ticker has no room for graphs and bars: it keeps the figures, rows and notes, in order, as one line.
    private static IEnumerable<WidgetPreviewFigure> _TickerFigures(object item) => item switch
    {
        WidgetPreviewFigure figure => [figure with { FontSize = 16 }],
        WidgetPreviewStats stats => stats.Figures.Select(figure => figure with { FontSize = 16 }),
        WidgetPreviewRow row => [new WidgetPreviewFigure(row.Badge ?? row.Left, row.Right.Length > 0 ? row.Right : row.Badge is null ? "" : row.Left, row.RightBrush, 16)],
        WidgetPreviewNote note => [new WidgetPreviewFigure("", note.Text, Dim, 12)],
        WidgetPreviewGraph graph => graph.Legend.Select(figure => new WidgetPreviewFigure("", figure.Text, figure.Brush, 16)),
        _ => []
    };

    private static WidgetPreviewLine _Straight(double y, double width) => new([new Point(0, y), new Point(width, y)], Baseline, 1, null);

    private static Color _Lighten(Color color)
    {
        static byte Toward(byte channel) => (byte)Math.Round(channel + (255 - channel) * 0.45);
        return Color.FromRgb(Toward(color.R), Toward(color.G), Toward(color.B));
    }
}
