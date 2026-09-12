using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Runs;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using Grpc.Core;
using RunsGrpc = EveUtils.Grpc.Runs;

namespace EveUtils.Server.Grpc;

public sealed class RunsGrpcService(ServerSessionService sessions, IRunSyncRepository repository, ConnectedClients connectedClients)
    : RunsGrpc.RunsBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles };

    public override async Task<RunActionReply> PushRun(PushRunRequest request, ServerCallContext context)
    {
        ServerSession session = await _AuthenticateAsync(context);
        RunWirePayload? payload = JsonSerializer.Deserialize<RunWirePayload>(request.PayloadJson, SerializerOptions);
        if (payload?.Run is null)
            return new RunActionReply { Accepted = false, Message = "Invalid run payload." };

        Run run = payload.Run.ToEntity();
        long characterId = session.SyncedCharacter?.EsiCharacterId ?? 0;
        if (run.CharacterId != characterId)
            return new RunActionReply { Accepted = false, Message = "A run can only be synced by its owner." };

        run.StartedAtUtc = _Anchor(run.StartedAtUtc, payload.SentAtUnixMilliseconds);
        run.StoppedAtUtc = run.StoppedAtUtc is { } stoppedAtUtc
            ? _Anchor(stoppedAtUtc, payload.SentAtUnixMilliseconds)
            : null;
        run.SyncState = EveUtils.Shared.Modules.Runs.Enums.RunSyncState.Synced;
        DateTime? pushedAtUtc = await repository.UpsertAsync(run, context.CancellationToken);
        if (pushedAtUtc is null)
            return new RunActionReply { Accepted = false, Message = "A newer run revision is already stored." };

        await _NotifyGroupAsync(run, context.CancellationToken);
        return new RunActionReply { Accepted = true, Message = "Run synced.", LastPushedAtUtc = pushedAtUtc.Value.ToString("O") };
    }

    /// <summary>
    /// Tells the group's other pilots a run of theirs changed here (ET-245), so their clients pull it instead of
    /// waiting for their own next publish — without this, whoever published first never saw the other's run. Sent to
    /// the characters holding a run in the group rather than to a fleet: the pull only answers those characters, and a
    /// run is usually saved after its fleet has already been concluded. Live only; a client that was offline pulls
    /// when it reconnects. The pusher is left out — its own client pulls right after this reply.
    /// </summary>
    private async Task _NotifyGroupAsync(Run run, CancellationToken cancellationToken)
    {
        if (run.GroupCode is not { } groupCode)
            return;

        IReadOnlyList<long> holders = await repository.ListGroupHoldersAsync(groupCode, cancellationToken);
        int[] recipients = [.. holders
            .Where(characterId => characterId != run.CharacterId && characterId is > 0 and <= int.MaxValue)
            .Select(characterId => (int)characterId)];
        if (recipients.Length == 0)
            return;

        EventEnvelope envelope = WireEnvelopeFactory.ToEnvelope(new RunGroupUpdatedEvent(new RunGroupUpdate(groupCode)));
        await connectedClients.SendToCharactersAsync(recipients, envelope, cancellationToken);
    }

    public override async Task<PullRunsReply> PullRuns(PullRunsRequest request, ServerCallContext context)
    {
        ServerSession session = await _AuthenticateAsync(context);
        if (!DateTime.TryParse(request.SinceUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime sinceUtc))
            return new PullRunsReply { Accepted = false, Message = "Invalid synchronization waterline." };

        long characterId = session.SyncedCharacter?.EsiCharacterId ?? 0;
        IReadOnlyList<Run> runs = await repository.ListChangedAsync(characterId, request.GroupCodes, sinceUtc, context.CancellationToken);
        var reply = new PullRunsReply { Accepted = true, Message = "Runs synchronized." };
        long sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        reply.PayloadJson.AddRange(runs.Select(run => JsonSerializer.Serialize(new RunWirePayload
        {
            Run = RunWireData.FromEntity(run),
            SentAtUnixMilliseconds = sentAt
        }, SerializerOptions)));
        return reply;
    }

    private async Task<ServerSession> _AuthenticateAsync(ServerCallContext context)
    {
        string? authorization = context.RequestHeaders.GetValue("authorization");
        string? token = authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : null;
        ServerSession? session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        return session ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Not authenticated — pair with the server first."));
    }
    private static DateTime _Anchor(DateTime sourceUtc, long sentAtUnixMilliseconds) =>
        AbyssalSpace.AnchorFromWireUtc(sourceUtc, sentAtUnixMilliseconds, DateTime.UtcNow);
}
