using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.Transport;

public interface IServerRunSyncClient
{
    Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
        string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default);

    Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
        string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
        CancellationToken cancellationToken = default);
}
