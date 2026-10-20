using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One activity in the BY ACTIVITY list (ET-517): what the pilot's own runs made of it in the range on screen (YOURS), the
/// typical run over the statistic window with its band, worst-to-best scale and ISK/hour (a server's shared figure when
/// there is one, else the pilot's own runs — and the row says which), and when it was last flown.
///
/// <para>Under the minimum of runs a figure is a dash with "3 of 5 needed" beneath it, never a number (ET-512 decision 5).
/// The text on a row is the plain ink: the dim one measured 3.3–4.4:1 on a selected row, so the row's secondary lines
/// inherit it from the button and lift with it, as the runs rows do.</para>
/// </summary>
public sealed partial class ActivityStatsRowViewModel : ViewModelBase
{
    private static readonly double MinimumRuns = EarningsPolicy.Default.MinimumRuns;

    public ActivityStatsRowViewModel(ActivityStats stats, ActivityStatsLabel label, DateTime nowUtc)
    {
        Stats = stats;
        Label = label;
        Basis = stats.Basis;
        NameText = label.NameText;
        TypeIcon = label.TypeIcon;
        MetaText = _MetaText(label, stats.LastFlownUtc, nowUtc);
        ShowsYours = stats.Yours is not null;
        (YoursText, YoursSub) = stats.Yours is { } yours ? _YoursOf(yours) : (string.Empty, string.Empty);
        RunsInRange = stats.Yours?.RunCount ?? 0;
        _RenderBasis();
    }

    public ActivityStats Stats { get; }

    /// <summary>A click on the row: the screen selects it and reads it out, wherever its pane is.</summary>
    public event Action<ActivityStatsRowViewModel>? SelectRequested;

    [RelayCommand]
    private void Select() => SelectRequested?.Invoke(this);

    public ActivityStatsKey Key => Stats.Key;

    public ActivityStatsLabel Label { get; }

    /// <summary>The figure the TYPICAL columns read now: the one the pilot switched to, else <see cref="ActivityStats.Basis"/>.</summary>
    public ExpectedEarnings? Basis { get; private set; }

    public string NameText { get; }

    public MaterialIconKind TypeIcon { get; }

    public string TypeText => Label.TypeText;

    /// <summary>Family, type and when the pilot last flew it — no location in it.</summary>
    public string MetaText { get; }

    public bool ShowsYours { get; }

    public string YoursText { get; }

    public string YoursSub { get; }

    public int RunsInRange { get; }

    [ObservableProperty] private string _typicalText = "—";
    [ObservableProperty] private string _typicalSub = string.Empty;
    [ObservableProperty] private string _basisTooltip = string.Empty;
    [ObservableProperty] private bool _isSharedBasis;
    [ObservableProperty] private bool _hasBand;
    [ObservableProperty] private double _bandStart;
    [ObservableProperty] private double _bandEnd;
    [ObservableProperty] private double _typicalMark;
    [ObservableProperty] private string _bandText = string.Empty;
    [ObservableProperty] private string _rangeTooltip = string.Empty;
    [ObservableProperty] private string _perHourText = "—";

    /// <summary>The LOOT column of the mockup. Nothing to open until the loot table exists (ET-519), so it is not on screen.</summary>
    public bool IsLootShown => false;

    /// <summary>The sort values: null for an activity with no figure, which sorts after every one that has.</summary>
    public decimal? TypicalValue => Basis?.Figures.Typical;

    public decimal? PerHourValue => Basis?.Figures.TypicalIskPerHour;

    public double? RunsValue => Basis?.Figures.EffectiveRunCount ?? (Stats.EffectiveRuns > 0 ? Stats.EffectiveRuns : null);

    public DateTime? LastFlownUtc => Stats.LastFlownUtc;

    /// <summary>Another server's figure, picked on the source chip: this row (and the detail) read it from now on.</summary>
    public void UseBasis(ExpectedEarnings basis)
    {
        Basis = basis;
        _RenderBasis();
        OnPropertyChanged(nameof(Basis));
        OnPropertyChanged(nameof(TypicalValue));
        OnPropertyChanged(nameof(PerHourValue));
        OnPropertyChanged(nameof(RunsValue));
    }

    private void _RenderBasis()
    {
        if (Basis is not { } basis)
        {
            _RenderNoFigure(Stats.EffectiveRuns);
            return;
        }

        EarningsFiguresDto figures = basis.Figures;
        IsSharedBasis = basis.Basis is EarningsBasis.AllPilots;
        BasisTooltip = IsSharedBasis
            ? $"All pilots · {basis.ServerName}"
            : OwnRunsReasonText.For(Stats.Reason) is { Length: > 0 } reason ? reason : "Your own runs";
        if (figures.Typical is not { } typical)
        {
            _RenderNoFigure(figures.EffectiveRunCount);
            return;
        }

        TypicalText = IskFormat.Compact(typical);
        TypicalSub = IsSharedBasis
            ? $"{figures.RunCount.ToString(CultureInfo.InvariantCulture)} runs · {basis.PilotCount.ToString(CultureInfo.InvariantCulture)} pilots"
            : $"your runs · {figures.RunCount.ToString(CultureInfo.InvariantCulture)}";
        PerHourText = figures.TypicalIskPerHour is { } perHour ? $"{IskFormat.Compact(perHour)}/h" : "—";
        decimal low = (figures.Band is EarningsBand.Deciles ? figures.P10 : figures.P25) ?? typical;
        decimal high = (figures.Band is EarningsBand.Deciles ? figures.P90 : figures.P75) ?? typical;
        decimal worst = figures.Worst?.YieldIsk ?? low;
        decimal best = figures.Best?.YieldIsk ?? high;
        var scale = new ExpectedEarningsViewModel.LogScale(worst, best);
        HasBand = true;
        BandStart = scale.Position(low);
        BandEnd = scale.Position(high);
        TypicalMark = scale.Position(typical);
        BandText = $"{IskFormat.Compact(low)} – {IskFormat.Compact(high)}";
        RangeTooltip = $"{(figures.Band is EarningsBand.Deciles ? "8 of 10 runs" : "Half of the runs")} {BandText} · worst {IskFormat.Compact(worst)} · best {IskFormat.Compact(best)}";
    }

    private void _RenderNoFigure(double effectiveRuns)
    {
        TypicalText = "—";
        TypicalSub = _NeededText(effectiveRuns);
        PerHourText = "—";
        HasBand = false;
        BandText = string.Empty;
        RangeTooltip = "Not enough runs for a range yet";
    }

    private static (string Text, string Sub) _YoursOf(EarningsFiguresDto yours) =>
        yours.Typical is { } typical
            ? (IskFormat.Compact(typical), $"{yours.RunCount.ToString(CultureInfo.InvariantCulture)} runs")
            : ("—", yours.RunCount == 0 ? string.Empty : _NeededText(yours.EffectiveRunCount));

    private static string _NeededText(double effectiveRuns) =>
        $"{Math.Min(MinimumRuns - 1, Math.Floor(effectiveRuns)).ToString("0", CultureInfo.InvariantCulture)} of {MinimumRuns.ToString("0", CultureInfo.InvariantCulture)} needed";

    private static string _MetaText(ActivityStatsLabel label, DateTime? lastFlownUtc, DateTime nowUtc)
    {
        string last = lastFlownUtc is { } at ? $"last flown {OutcomeSharingServerViewModel.Age(nowUtc - at)}" : string.Empty;
        return string.Join(" · ", new[] { label.FamilyText, label.TypeText, last }.Where(part => part.Length > 0));
    }
}
