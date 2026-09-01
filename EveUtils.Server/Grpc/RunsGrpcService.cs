using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Runs;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using Grpc.Core;
using RunsGrpc = EveUtils.Grpc.Runs;

namespace EveUtils.Server.Grpc;

public sealed class RunsGrpcService(ServerSessionService sessions, IRunSyncRepository repository) : RunsGrpc.RunsBase
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
        return new RunActionReply { Accepted = true, Message = "Run synced.", LastPushedAtUtc = pushedAtUtc.Value.ToString("O") };
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
        AbyssalSpace.AnchorFromWire(new DateTimeOffset(sourceUtc.ToUniversalTime()).ToUnixTimeMilliseconds(), sentAtUnixMilliseconds, DateTime.UtcNow)
        ?? sourceUtc;
}
