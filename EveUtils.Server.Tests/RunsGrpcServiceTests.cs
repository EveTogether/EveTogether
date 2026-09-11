using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Server.Runs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-244, measured: Raymond's abyssal runs landed two hours early after Jithran pulled them. <c>PushRun</c> and
/// <c>PullRuns</c> share the exact same <c>_Anchor</c> pattern the client applier had — a run time read back from a
/// database column comes back <see cref="DateTimeKind.Unspecified"/> regardless of its real UTC value, and
/// <c>DateTime.ToUniversalTime()</c> reads that as local time, shifting it by whatever timezone the reading machine
/// happens to run in. On a server this only stayed hidden because a server commonly runs in UTC, where the shift is
/// zero; it is the same latent bug as the client-side one, not a different one.
/// </summary>
public sealed class RunsGrpcServiceTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task PushRun_UnspecifiedKindOnTheWire_IsNotShiftedByTheServersOwnTimeZone()
    {
        Assert.NotEqual(TimeSpan.Zero, TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, cancellationToken);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, cancellationToken);
        var service = new RunsGrpcService(sessions, new ServerRunSyncRepository((IDbContextFactory<ServerDbContext>)_factory));

        var startedAtUtc = new DateTime(2026, 9, 11, 18, 49, 42, DateTimeKind.Unspecified);
        var stoppedAtUtc = new DateTime(2026, 9, 11, 18, 56, 39, DateTimeKind.Unspecified);
        var run = new Run
        {
            Id = Guid.CreateVersion7(),
            CharacterId = 90250177,
            GroupCode = "HF-Z6U3",
            ActivityKind = ActivityKind.Abyssal,
            State = RunState.Saved,
            StartedAtUtc = startedAtUtc,
            StoppedAtUtc = stoppedAtUtc,
            SavedAtUtc = stoppedAtUtc,
            SiteTypeId = 1234,
            SyncState = RunSyncState.Pending,
            Revision = 2
        };
        var payload = new RunWirePayload { Run = RunWireData.FromEntity(run), SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        string json = JsonSerializer.Serialize(payload);
        // An unspecified DateTime serializes without a "Z"/offset suffix — the precondition this bug needs.
        Assert.Contains("2026-09-11T18:49:42", json);
        Assert.DoesNotContain("2026-09-11T18:49:42Z", json);

        var reply = await service.PushRun(new PushRunRequest { PayloadJson = json }, Context(issued.AccessToken));

        Assert.True(reply.Accepted, reply.Message);
        await using ServerDbContext db = ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContext();
        Run stored = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        DateTime storedStoppedAtUtc = stored.StoppedAtUtc ?? throw new InvalidOperationException("The stopped time was not stored.");
        Assert.InRange(stored.StartedAtUtc, startedAtUtc.AddSeconds(-5), startedAtUtc.AddSeconds(5));
        Assert.InRange(storedStoppedAtUtc, stoppedAtUtc.AddSeconds(-5), stoppedAtUtc.AddSeconds(5));
    }

    private static ServerCallContext Context(string bearer)
    {
        var headers = new Metadata { { "authorization", $"Bearer {bearer}" } };
        return new HeadersOnlyCallContext(headers);
    }

    /// <summary>Only the request headers and cancellation token are read by <c>PushRun</c>, so the rest of the
    /// context is left unimplemented rather than pulling in Grpc.Core.Testing for one factory call.</summary>
    private sealed class HeadersOnlyCallContext(Metadata headers) : ServerCallContext
    {
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override string MethodCore => "test";
        protected override string HostCore => "test";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => throw new NotSupportedException();
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
