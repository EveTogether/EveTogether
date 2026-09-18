using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;

namespace EveUtils.Client.Runs;

/// <summary>A server tab's read (ET-311): what one server holds in a window for this machine's characters, built
/// into rows without touching the local store — which is <see cref="RunSynchronizationService"/>'s job, not this
/// one's. Asked per character, since a session is per (server, character) and each answers for its own groups; a run
/// two of them may both see comes back once.</summary>
public sealed class ServerRunsReader(IServerRunSyncClient client, IDispatcher dispatcher) : IScopedService
{
    public async Task<Result<IReadOnlyList<ServerActivityDto>>> ReadAsync(string serverAddress, IReadOnlyList<long> characterIds,
        DateTime fromUtc, DateTime toUtc, long? fleetId, IReadOnlyList<long>? ownCharacterIds, CancellationToken cancellationToken = default)
    {
        Dictionary<Guid, RunWirePayload> held = [];
        foreach (long characterId in characterIds)
        {
            (bool accepted, string message, IReadOnlyList<RunWirePayload> runs) =
                await client.ListPublishedAsync(serverAddress, fromUtc, toUtc, characterId, cancellationToken);
            if (!accepted)
                return Result<IReadOnlyList<ServerActivityDto>>.Failure(
                    new ResultMessage(MessageSeverity.Error, MessageCodes.ServerError, message, "Runs"));

            foreach (RunWirePayload payload in runs)
                held.TryAdd(payload.Run.Id, payload);
        }

        return await dispatcher.Query(new GetServerActivityOverviewQuery(serverAddress, [.. held.Values], fleetId, ownCharacterIds),
            cancellationToken);
    }
}
