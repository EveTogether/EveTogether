using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Runs;
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

        long characterId = session.SyncedCharacter?.EsiCharacterId ?? 0;
        if (payload.Run.CharacterId != characterId)
            return new RunActionReply { Accepted = false, Message = "A run can only be synced by its owner." };

        DateTime pushedAtUtc = await repository.UpsertAsync(payload.Run, context.CancellationToken);
        return new RunActionReply { Accepted = true, Message = "Run synced.", LastPushedAtUtc = pushedAtUtc.ToString("O") };
    }

    public override async Task<PullRunsReply> PullRuns(PullRunsRequest request, ServerCallContext context)
    {
        await _AuthenticateAsync(context);
        if (!DateTime.TryParse(request.SinceUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime sinceUtc))
            return new PullRunsReply { Accepted = false, Message = "Invalid synchronization waterline." };

        IReadOnlyList<Run> runs = await repository.ListChangedAsync(request.GroupCodes, sinceUtc, context.CancellationToken);
        var reply = new PullRunsReply { Accepted = true, Message = "Runs synchronized." };
        long sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        reply.PayloadJson.AddRange(runs.Select(run => JsonSerializer.Serialize(new RunWirePayload { Run = run, SentAtUnixMilliseconds = sentAt }, SerializerOptions)));
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
}
