using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="IFleetMetricsLauncher"/> double for headless tests: records every launch instead of opening the
/// metrics window (the real launcher needs the dialog/overlay layer). Lets a test assert that a toast's
/// "Open metrics" button targeted the right fleet.
/// </summary>
public sealed class RecordingFleetMetricsLauncher : IFleetMetricsLauncher
{
    public List<(string ServerAddress, long FleetId, int ActingCharacterId)> LaunchCalls { get; } = new();

    public Task LaunchAsync(string serverAddress, long fleetId, int actingCharacterId, FleetInfo? fleet = null,
        CancellationToken cancellationToken = default)
    {
        LaunchCalls.Add((serverAddress, fleetId, actingCharacterId));
        return Task.CompletedTask;
    }
}
