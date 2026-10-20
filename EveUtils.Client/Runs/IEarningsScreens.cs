using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Client.Runs;

/// <summary>The two screens the run window's EXPECTED EARNINGS panel leads to (ET-521): the figures of its activity in
/// the runs overview, and the setting that makes them use every pilot's runs.</summary>
public interface IEarningsScreens
{
    /// <summary>The runs overview on BY ACTIVITY with <paramref name="key"/> selected, whether or not it is open already.</summary>
    Task OpenStatsAsync(ActivityStatsKey key);

    /// <summary>Settings, on Privacy &amp; Sharing, where the pilot turns on sharing the outcome of their runs.</summary>
    Task OpenSharingSettingsAsync();
}
