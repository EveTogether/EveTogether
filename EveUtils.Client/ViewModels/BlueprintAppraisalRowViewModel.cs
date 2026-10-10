using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One pasted blueprint in the appraisal tool (ET-502): the runs, ME and TE the pilot sets, and the build they value at.
/// Runs and ME change the materials and so ask the owner for a fresh appraisal; TE only changes the job time.
/// </summary>
public sealed partial class BlueprintAppraisalRowViewModel : ObservableObject
{
    private readonly Func<int, string> _nameOf;

    public BlueprintAppraisalRowViewModel(SdeBlueprintManufacturing blueprint, string name, Func<int, string> nameOf)
    {
        _nameOf = nameOf;
        TypeId = blueprint.BlueprintTypeId;
        Name = name;
        MaxRuns = Math.Max(1, blueprint.MaxProductionLimit);
        BaseTimeSeconds = blueprint.TimeSeconds;
        ProductName = nameOf(blueprint.ProductTypeId);
    }

    /// <summary>Raised when runs or ME change, so the owner values the build again.</summary>
    public event Action<BlueprintAppraisalRowViewModel>? RebuildRequested;

    public int TypeId { get; }

    public string Name { get; }

    public string ProductName { get; }

    /// <summary>The most runs one job of this blueprint takes (the SDE's production limit).</summary>
    public int MaxRuns { get; }

    public int BaseTimeSeconds { get; }

    [ObservableProperty] private int _runs = 1;

    [ObservableProperty] private int _materialEfficiency;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildTimeText), nameof(SettingsText))]
    private int _timeEfficiency;

    [ObservableProperty] private bool _isChecked;

    [ObservableProperty] private bool _isSelected;

    public BlueprintAppraisal? Appraisal { get; private set; }

    public IReadOnlyList<BlueprintMaterialRowViewModel> Materials { get; private set; } = [];

    public decimal? Profit => Appraisal?.Profit;

    public bool HasPrice => Profit is not null;

    public bool IsProfitable => Profit > 0;

    public bool IsLossMaking => Profit < 0;

    public string ProfitText => Profit is { } profit ? (profit > 0 ? "+" : string.Empty) + IskFormat.Whole(profit) : "no price";

    public string SettingsText => $"{Runs.ToString("N0", CultureInfo.InvariantCulture)} run{(Runs == 1 ? "" : "s")} · ME {MaterialEfficiency} · TE {TimeEfficiency}";

    public string ProductText => Appraisal is { } appraisal
        ? $"{appraisal.ProductQuantity.ToString("N0", CultureInfo.InvariantCulture)} × {ProductName}"
        : ProductName;

    public string ProductValueText => IskFormat.WholeOrNoPrice(Appraisal?.ProductValue);

    public string MaterialsCostText => IskFormat.WholeOrNoPrice(Appraisal?.MaterialsCost);

    public string JobCostText => Appraisal is { } appraisal ? IskFormat.Whole(appraisal.JobCost) : "no price";

    /// <summary>Before skills and structure bonuses — ET knows neither.</summary>
    public string BuildTimeText => _Duration(BlueprintBuildCalculator.BuildSeconds(BaseTimeSeconds, Runs, TimeEfficiency));

    public void Apply(BlueprintAppraisal appraisal)
    {
        Appraisal = appraisal;
        Materials = [.. appraisal.Materials.Select(material => new BlueprintMaterialRowViewModel(_nameOf(material.TypeId), material))];
        OnPropertyChanged(string.Empty);
    }

    partial void OnRunsChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, MaxRuns);
        if (clamped != value)
        {
            Runs = clamped;
            return;
        }
        RebuildRequested?.Invoke(this);
    }

    partial void OnMaterialEfficiencyChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, BlueprintBuildCalculator.MaxMaterialEfficiency);
        if (clamped != value)
        {
            MaterialEfficiency = clamped;
            return;
        }
        RebuildRequested?.Invoke(this);
    }

    partial void OnTimeEfficiencyChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, BlueprintBuildCalculator.MaxTimeEfficiency);
        if (clamped != value)
            TimeEfficiency = clamped;
    }

    private static string _Duration(int seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalDays >= 1
            ? $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m"
            : time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : $"{time.Minutes}m {time.Seconds}s";
    }
}
