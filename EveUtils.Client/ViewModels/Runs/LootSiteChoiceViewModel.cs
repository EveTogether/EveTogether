using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Earnings;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One site on the list the loot window opens with when it has no site (ET-519, ET-521): flown or not. The
/// name is marked as a location for OPSEC (ET-417); the search matches on the unmarked text.</summary>
public sealed partial class LootSiteChoiceViewModel(LootSiteChoice choice, ActivityStatsLabel label, DateTime nowUtc,
    Action<ActivityStatsKey> pick) : ViewModelBase
{
    public ActivityStatsKey Key { get; } = choice.Key;

    public string NameText { get; } = label.NameText;

    public string SearchText { get; } = label.SearchText;

    public string TypeText { get; } = label.TypeText;

    public MaterialIconKind TypeIcon { get; } = label.TypeIcon;

    /// <summary>What there is to go on: "12 runs with loot" from the table's minimum of runs, "3 of 5 needed" below it (ET-521).</summary>
    public string MetaText { get; } = string.Join(" · ", new[]
    {
        label.FamilyText,
        choice.LootRunCount >= LootTablePolicy.Default.MinimumRuns
            ? $"{choice.LootRunCount.ToString(CultureInfo.InvariantCulture)} runs with loot"
            : $"{choice.LootRunCount.ToString(CultureInfo.InvariantCulture)} of {LootTablePolicy.Default.MinimumRuns.ToString("0", CultureInfo.InvariantCulture)} needed",
        choice.LastFlownUtc is { } flown
            ? $"last flown {Math.Max(0, (int)(nowUtc - flown).TotalDays).ToString(CultureInfo.InvariantCulture)} d ago"
            : string.Empty
    }.Where(part => part.Length > 0));

    [RelayCommand]
    private void Pick() => pick(Key);
}
