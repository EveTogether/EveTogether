using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Controls;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// TIMELINE on the detail screen (ET-468): the picked pilot's stored series over the run, per 5 s, in the fixed combat
/// inks and two lanes per unit. COMBAT reads the store; this follows whatever pilot it shows.
/// </summary>
public sealed partial class TimelineDetailSectionViewModel : RunDetailSection
{
    private readonly CombatTelemetryChoice _choice;

    public TimelineDetailSectionViewModel(RunDetailSectionServices services) : base(RunSectionId.Timeline, "TIMELINE")
    {
        _choice = services.Combat ?? new CombatTelemetryChoice(services.Dispatcher, services.OwnCharacterIds);
        _choice.PropertyChanged += _OnChoiceChanged;
        HeaderSummary = "not recorded";
        TimelineEmptyText = CombatDetailSectionViewModel.NotRecordedText;
    }

    public ObservableCollection<CombatLegendItem> Legend { get; } = [];

    [ObservableProperty] private CombatChartModel? _chart;
    [ObservableProperty] private string? _timelineEmptyText;

    public override bool HasContent => _choice.HasAny;

    public override void Apply(RunDetailSectionInput input)
    {
    }

    private void _OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CombatTelemetryChoice.Shown))
        {
            return;
        }

        Legend.Clear();
        if (_choice.Shown is not { } timeline)
        {
            Chart = null;
            HeaderSummary = "not recorded";
            TimelineEmptyText = CombatDetailSectionViewModel.NotRecordedText;
            return;
        }

        CombatChartModel chart = CombatChartModel.Of(timeline);
        Chart = chart;
        TimelineEmptyText = null;
        foreach ((CombatSeriesKind kind, string name) in Names)
        {
            bool present = chart.Buckets.ContainsKey(kind);
            Legend.Add(new CombatLegendItem(present ? name : $"{name} · none in this run", CombatTimelineChart.InkOf(kind), present));
        }

        long peak = chart.Buckets.GetValueOrDefault(CombatSeriesKind.DmgOut, []).DefaultIfEmpty().Max() / chart.BucketSeconds;
        HeaderSummary = $"peak {peak.ToString("N0", CultureInfo.InvariantCulture)} dps out · 1 s samples, shown per {chart.BucketSeconds} s";
    }

    private static readonly (CombatSeriesKind Kind, string Name)[] Names =
    [
        (CombatSeriesKind.DmgOut, "DPS out"),
        (CombatSeriesKind.DmgIn, "DPS in"),
        (CombatSeriesKind.RepOut, "reps out"),
        (CombatSeriesKind.RepIn, "reps in"),
        (CombatSeriesKind.NeutIn, "neut on you (GJ/s)"),
        (CombatSeriesKind.NeutOut, "neut out (GJ/s)"),
        (CombatSeriesKind.CapIn, "cap in"),
        (CombatSeriesKind.CapOut, "cap out")
    ];
}
