using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.Transport;

public interface IServerRunSyncClient
{
    Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
        string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default);

    Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
        string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
        CancellationToken cancellationToken = default);

    /// <summary>The runs the server holds for <paramref name="actingCharacterId"/> started in [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>) (ET-311) — a server tab's own read, never written to the local store.</summary>
    Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> ListPublishedAsync(
        string serverAddress, DateTime fromUtc, DateTime toUtc, long actingCharacterId, CancellationToken cancellationToken = default);
}
