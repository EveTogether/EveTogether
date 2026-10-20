using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Earnings;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One activity the pilot has flown, on the list the loot window opens with when it has no site (ET-519). The
/// name is marked as a location for OPSEC (ET-417); the search matches on the unmarked text.</summary>
public sealed partial class LootSiteChoiceViewModel(LootSiteChoice choice, ActivityStatsLabel label, DateTime nowUtc,
    Action<ActivityStatsKey> pick) : ViewModelBase
{
    public ActivityStatsKey Key { get; } = choice.Key;

    public string NameText { get; } = label.NameText;

    public string SearchText { get; } = label.SearchText;

    public string TypeText { get; } = label.TypeText;

    public MaterialIconKind TypeIcon { get; } = label.TypeIcon;

    public string MetaText { get; } = string.Join(" · ", new[]
    {
        label.FamilyText,
        $"{choice.RunCount.ToString(CultureInfo.InvariantCulture)} {(choice.RunCount == 1 ? "run" : "runs")}",
        $"last flown {Math.Max(0, (int)(nowUtc - choice.LastFlownUtc).TotalDays).ToString(CultureInfo.InvariantCulture)} d ago"
    }.Where(part => part.Length > 0));

    [RelayCommand]
    private void Pick() => pick(Key);
}
