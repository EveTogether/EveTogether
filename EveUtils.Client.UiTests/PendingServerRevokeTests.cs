using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Shared.Data;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Transport.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-344: a decouple the server never heard about (it was unreachable) is remembered and repeated on the next
/// connection, instead of leaving the server holding a session and an EVE token for a character the player let go of.
/// </summary>
public sealed class PendingServerRevokeTests : IDisposable
{
    private const string Server = "eve.example:7443";
    private const int CharacterId = 90382598;

    private readonly ClientDbContextFactory _factory = new();
    private readonly EfClientSessionStore _sessions;
    private readonly EfPendingServerRevokeStore _pending;
    private readonly ScriptedRevoker _revoker = new();
    private readonly ServerCouplingService _coupling;
    private readonly PendingServerRevokeFlusher _flusher;

    public PendingServerRevokeTests()
    {
        _sessions = new EfClientSessionStore(_factory, new NoPinsTrustStore());
        _pending = new EfPendingServerRevokeStore(_factory);
        _coupling = new ServerCouplingService(_sessions, _revoker, _pending, new FakeRemoteBusConnector());
        _flusher = new PendingServerRevokeFlusher(_pending, _revoker, NullLogger<PendingServerRevokeFlusher>.Instance);
    }

    [Fact]
    public async Task Decouple_WhenTheServerIsUnreachable_RemembersTheRevoke_AndStillDropsTheLocalSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await _CoupleAsync("access-1", ct);
        _revoker.Outcome = ServerRevokeOutcome.Unreachable;

        await _coupling.DecoupleCharacterAsync(Server, CharacterId, ct);

        Assert.Null(await _sessions.LoadForCharacterAsync(Server, CharacterId, ct));
        var queued = Assert.Single(await _pending.ListAsync(Server, ct));
        Assert.Equal("access-1", queued.AccessToken);
        Assert.Equal(CharacterId, queued.CharacterId);
    }

    [Theory]
    [InlineData(ServerRevokeOutcome.Revoked)]
    [InlineData(ServerRevokeOutcome.NoSuchSession)]
    public async Task Decouple_WhenTheServerAnswers_QueuesNothing(ServerRevokeOutcome outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await _CoupleAsync("access-1", ct);
        _revoker.Outcome = outcome;

        await _coupling.DecoupleCharacterAsync(Server, CharacterId, ct);

        Assert.Empty(await _pending.ListAsync(Server, ct));
        Assert.Equal(["access-1"], _revoker.Attempts);
    }

    [Fact]
    public async Task Flush_OnceTheServerIsReachable_RepeatsTheRevokeAndClearsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await _CoupleAsync("access-1", ct);
        _revoker.Outcome = ServerRevokeOutcome.Unreachable;
        await _coupling.DecoupleCharacterAsync(Server, CharacterId, ct);
        _revoker.Attempts.Clear();

        _revoker.Outcome = ServerRevokeOutcome.Revoked;
        await _flusher.FlushAsync(Server, ct);

        Assert.Equal(["access-1"], _revoker.Attempts);
        Assert.Empty(await _pending.ListAsync(Server, ct));
    }

    [Fact]
    public async Task Flush_WhileTheServerIsStillUnreachable_KeepsTheRevokeQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        await _CoupleAsync("access-1", ct);
        _revoker.Outcome = ServerRevokeOutcome.Unreachable;
        await _coupling.DecoupleCharacterAsync(Server, CharacterId, ct);

        await _flusher.FlushAsync(Server, ct);

        Assert.Single(await _pending.ListAsync(Server, ct));
    }

    [Fact]
    public async Task Flush_WhenTheServerHasNoSuchSessionAnyMore_ClearsTheRevoke()
    {
        var ct = TestContext.Current.CancellationToken;
        await _pending.QueueAsync(Server, CharacterId, "access-swept", ct);
        _revoker.Outcome = ServerRevokeOutcome.NoSuchSession;

        await _flusher.FlushAsync(Server, ct);

        Assert.Empty(await _pending.ListAsync(Server, ct));
    }

    [Fact]
    public async Task Flush_OnlyTouchesTheServerItIsCalledFor()
    {
        var ct = TestContext.Current.CancellationToken;
        await _pending.QueueAsync(Server, CharacterId, "access-here", ct);
        await _pending.QueueAsync("other.example:7443", CharacterId, "access-elsewhere", ct);
        _revoker.Outcome = ServerRevokeOutcome.Revoked;

        await _flusher.FlushAsync(Server, ct);

        Assert.Equal(["access-here"], _revoker.Attempts);
        Assert.Single(await _pending.ListAsync("other.example:7443", ct));
    }

    [Fact]
    public async Task Queue_TheSameSessionTwice_KeepsOneEntry()
    {
        var ct = TestContext.Current.CancellationToken;

        await _pending.QueueAsync(Server, CharacterId, "access-1", ct);
        await _pending.QueueAsync(Server, CharacterId, "access-1", ct);

        Assert.Single(await _pending.ListAsync(Server, ct));
    }

    private Task _CoupleAsync(string accessToken, CancellationToken ct) =>
        _sessions.SaveAsync(Server, new ClientSessionTokens(accessToken, "refresh-1", "Abnoba Auscent", CharacterId, 0), ct);

    public void Dispose() => _factory.Dispose();

    private sealed class ScriptedRevoker : IServerSessionRevoker
    {
        public ServerRevokeOutcome Outcome { get; set; } = ServerRevokeOutcome.Revoked;
        public List<string> Attempts { get; } = [];

        public Task<ServerRevokeOutcome> RevokeAsync(string serverAddress, string sessionToken, CancellationToken cancellationToken = default)
        {
            Attempts.Add(sessionToken);
            return Task.FromResult(Outcome);
        }
    }

    private sealed class NoPinsTrustStore : IServerTrustStore
    {
        public string? GetFingerprint(string serverAddress) => null;

        public void Pin(string serverAddress, string fingerprint)
        {
        }
    }

    private sealed class ClientDbContextFactory : IDbContextFactory<SharedDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly DbContextOptions<ClientDbContext> _options;

        public ClientDbContextFactory()
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<ClientDbContext>().UseSqlite(_connection).Options;
            using var context = new ClientDbContext(_options);
            context.Database.EnsureCreated();
        }

        public SharedDbContext CreateDbContext() => new ClientDbContext(_options);

        public void Dispose() => _connection.Dispose();
    }
}
