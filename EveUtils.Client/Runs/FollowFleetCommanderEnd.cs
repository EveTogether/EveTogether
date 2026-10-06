using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Runs;

/// <summary>
/// Whether this pilot's run follows the commander's SAVE or DISCARD of a fleet run, after a countdown (ET-458). The
/// pilot's own choice wins; until they make one it follows the automatic-join setting, since someone who lets the
/// commander start their run most likely wants the commander to end it too.
/// </summary>
public static class FollowFleetCommanderEnd
{
    public const string SettingKey = "fleet.run-window.follow-commander-end";

    public static readonly TimeSpan Countdown = TimeSpan.FromSeconds(10);

    public static async Task<bool> IsOnAsync(
        ISettingRepository settings, long fleetId, CancellationToken cancellationToken = default) =>
        Resolve((await settings.ListAsync(cancellationToken))
            .GroupBy(setting => setting.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal), fleetId);

    public static bool Resolve(IReadOnlyDictionary<string, string> values, long fleetId) =>
        FleetRunWindowPresenter.AsBool(values.GetValueOrDefault(SettingKey))
        ?? FleetRunWindowPresenter.AsBool(values.GetValueOrDefault(FleetRunWindowPresenter.PerFleetAutoOpenSettingKey(fleetId)))
        ?? FleetRunWindowPresenter.AsBool(values.GetValueOrDefault(FleetRunWindowPresenter.AutoOpenSettingKey))
        ?? false;
}
