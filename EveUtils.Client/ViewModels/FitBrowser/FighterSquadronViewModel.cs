using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Fighters;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One fighter squadron on the Fighter Bay panel — a single squadron of a fighter type, either loaded in a launch tube
/// (<see cref="TubeIndex"/> set) or sitting in the bay as a reserve. A launched squadron's <see cref="ActiveCount"/> (the
/// in-game per-tube "- / +", 1..<see cref="SquadronSize"/>) is how many of its fighters are firing — it drives the
/// squadron DPS and the active gauge fill. The whole squadron occupies <see cref="BayVolume"/> in the bay whether or
/// not it is launched.
/// </summary>
public sealed partial class FighterSquadronViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;

    public int TypeId { get; }
    public string Name { get; }
    public FighterKind Kind { get; }
    public int SquadronSize { get; }
    public bool DealsDamage { get; }

    /// <summary>The bay volume this squadron occupies: the type's per-fighter volume times the squadron size.</summary>
    public double BayVolume { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLabel))]
    [NotifyPropertyChangedFor(nameof(ActiveFraction))]
    [NotifyPropertyChangedFor(nameof(ActiveDash))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private int _activeCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLaunched))]
    private int? _tubeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;
    public bool IsLaunched => TubeIndex is not null;
    public string ActiveLabel => $"{ActiveCount}/{SquadronSize}";

    /// <summary>The active fraction (0..1) the launched-squadron gauge fills to.</summary>
    public double ActiveFraction => SquadronSize > 0 ? (double)ActiveCount / SquadronSize : 0;

    // The green active-ring's stroke dash: a single visible arc covering ActiveFraction of the circumference, then a gap
    // that swallows the rest. Dash lengths are multiples of the stroke thickness, so the circumference is computed from
    // the tube tile's gauge size (diameter 46, thickness 4) the XAML draws it at.
    private const double GaugeDiameter = 46, GaugeThickness = 4;

    /// <summary>The <c>StrokeDashArray</c> for the active-ring Ellipse, sized to <see cref="ActiveFraction"/>.</summary>
    public AvaloniaList<double> ActiveDash
    {
        get
        {
            var circumference = Math.PI * (GaugeDiameter - GaugeThickness) / GaugeThickness;
            return [ActiveFraction * circumference, circumference];
        }
    }

    public FighterSquadronViewModel(FighterType type, ITypeImageProvider? images = null)
    {
        TypeId = type.TypeId;
        Name = type.Name;
        Kind = type.Kind;
        SquadronSize = type.SquadronMaxSize;
        DealsDamage = type.DealsDamage;
        BayVolume = type.Volume * type.SquadronMaxSize;
        _activeCount = type.SquadronMaxSize;   // a freshly loaded squadron launches at full strength
        _images = images;
    }

    // The launched squadron's resolved per-fighter readout (set after each recompute); null falls the tooltip back to just
    // the name + active line. Damage scales with the active fighter count; the ranges are the type's engagement envelope.
    private FighterContribution? _contribution;

    public void SetContribution(FighterContribution? contribution)
    {
        _contribution = contribution;
        OnPropertyChanged(nameof(Tooltip));
    }

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { $"{Name} — {ActiveLabel} active" };
            if (_contribution is { } contribution)
            {
                if (contribution.DpsPerFighter > 0)
                    lines.Add($"Damage Per Second {contribution.DpsPerFighter * ActiveCount:0.0}");
                if (Math.Round(contribution.OptimalRange / 1000.0, 1) > 0)
                    lines.Add($"Optimal {contribution.OptimalRange / 1000.0:0.0} km");
                if (Math.Round(contribution.FalloffRange / 1000.0, 1) > 0)
                    lines.Add($"Falloff {contribution.FalloffRange / 1000.0:0.0} km");
                if (Math.Round(contribution.SalvoRange / 1000.0, 1) > 0)
                    lines.Add($"Salvo range {contribution.SalvoRange / 1000.0:0.0} km");
                if (contribution.Ewar is { } ewar)
                    lines.Add(_FormatEwar(ewar));
            }
            return string.Join("\n", lines);
        }
    }

    // A support fighter's EWAR readout (informational — target-less sim): kind + strength + optimal/falloff range.
    private static string _FormatEwar(FighterEwar ewar)
    {
        var range = ewar.FalloffRange > 0
            ? $"{ewar.OptimalRange / 1000.0:0.0} km (+{ewar.FalloffRange / 1000.0:0.0} falloff)"
            : $"{ewar.OptimalRange / 1000.0:0.0} km";
        return ewar.Kind switch
        {
            FighterEwarKind.EnergyNeutralizer => $"Energy neutralizer {ewar.Strength:0} GJ · {range}",
            FighterEwarKind.Ecm => $"ECM strength {ewar.Strength:0.0} · {range}",
            FighterEwarKind.WarpDisruption => $"Warp disruption {ewar.Strength:0.0} pt · {range}",
            FighterEwarKind.StasisWeb => $"Stasis web {ewar.Strength:0}% · {range}",
            _ => string.Empty
        };
    }

    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
