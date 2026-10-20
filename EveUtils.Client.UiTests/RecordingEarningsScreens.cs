using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Client.UiTests;

/// <summary>Stands in for the screens the earnings panel leads to (ET-521): remembers what was asked and opens nothing.</summary>
internal sealed class RecordingEarningsScreens : IEarningsScreens
{
    public List<ActivityStatsKey> Opened { get; } = [];

    public int SharingSettingsOpened { get; private set; }

    public Task OpenStatsAsync(ActivityStatsKey key)
    {
        Opened.Add(key);
        return Task.CompletedTask;
    }

    public Task OpenSharingSettingsAsync()
    {
        SharingSettingsOpened++;
        return Task.CompletedTask;
    }
}
