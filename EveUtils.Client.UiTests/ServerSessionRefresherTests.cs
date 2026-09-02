using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Shared.Transport;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The rules that decide whether a pilot keeps their server pairing (ET-121).
///
/// <para>Two characters lost theirs twice in two days, on two machines, because a single refused
/// <c>Session.Refresh</c> deleted the stored session from disk — after which the connect loop found nothing, reported
/// "not paired" and stopped for good. Nothing about that is visible from the outside: no dialog, no log line, just a
/// character that is quietly no longer coupled. So the invariants get a test even though the transport around them
/// does not: a rejection changes nothing on disk, and one expiry causes one rotation however many callers noticed
/// it at once.</para>
/// </summary>
public class ServerSessionRefresherTests
{
    private const string Server = "https://eve-together.com:7443";
    private const int Character = 90250177;

    [Fact]
    public async Task ARejectedRefresh_LeavesTheStoredPairingExactlyWhereItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"));
        var refresher = new ServerSessionRefresher(RefreshCall.AlwaysRefuses(), store);

        var (outcome, _) = await refresher.TryRefreshAsync(Server, Character, cancellationToken: ct);

        Assert.Equal(ServerSessionRefreshOutcome.Rejected, outcome);
        Assert.Equal(0, store.RemoveCalls); // the whole ticket: one refusal is not a reason to unpair anybody
        Assert.Equal(0, store.SaveCalls);
        var kept = await store.LoadForCharacterAsync(Server, Character, ct);
        Assert.Equal("refresh-1", kept!.RefreshToken);
    }

    [Fact]
    public async Task AnUnreachableServer_KeepsThePairingToo_AndSaysItIsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"));
        var refresher = new ServerSessionRefresher(RefreshCall.Throws(), store);

        var (outcome, _) = await refresher.TryRefreshAsync(Server, Character, cancellationToken: ct);

        // Reached-and-refused and could-not-be-reached must never collapse into one verdict: the second is a network
        // hiccup, and reopening the app on a bad connection used to be enough to trip it.
        Assert.Equal(ServerSessionRefreshOutcome.Unavailable, outcome);
        Assert.Equal(0, store.RemoveCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task ASuccessfulRefresh_PersistsTheRotatedPair_BeforeAnyoneCanUseIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"));
        var refresher = new ServerSessionRefresher(RefreshCall.Rotating(), store);

        var (outcome, session) = await refresher.TryRefreshAsync(Server, Character, cancellationToken: ct);

        Assert.Equal(ServerSessionRefreshOutcome.Refreshed, outcome);
        Assert.Equal("access-2", session!.AccessToken);
        var stored = await store.LoadForCharacterAsync(Server, Character, ct);
        Assert.Equal("refresh-2", stored!.RefreshToken); // on disk, not just in the caller's hand
        Assert.Equal(Character, stored.CharacterId);     // the character mapping survives the rotation
    }

    /// <summary>
    /// The refresh token rotates, so two refreshes in flight for one session leave the loser holding a token the
    /// server has already replaced — which the old code read as "genuinely expired" and paid for with the pairing.
    /// <c>ServerConnection</c> guarded its own two callers and <c>ServerSessionRefresher</c> guarded nothing, so a
    /// mass reconnect (a server deploy, a laptop waking up) had every unary client's refresh-on-401 racing the
    /// connect loop's. One gate now covers all of them.
    /// </summary>
    [Fact]
    public async Task ManyCallersNoticingOneExpiry_CauseOneRotation()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"));
        var call = RefreshCall.Rotating();
        var refresher = new ServerSessionRefresher(call, store);

        // Every caller saw the same access token fail — the shape of a mass reconnect.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => refresher.TryRefreshAsync(Server, Character, "access-1", ct)));

        Assert.All(outcomes, o => Assert.Equal(ServerSessionRefreshOutcome.Refreshed, o.Outcome));
        Assert.Equal(1, call.Calls);
        Assert.All(outcomes, o => Assert.Equal("access-2", o.Session!.AccessToken)); // all holding the live one
    }

    /// <summary>
    /// The gate is per (server, character), and so is the store row. Several characters coupled to one server
    /// refresh at the same moment on every mass reconnect, and each has to come out of it holding its own rotated
    /// pair — one overwriting another's row would leave that character with a token the server has replaced, which
    /// is precisely the stale-token state that costs a pairing.
    /// </summary>
    [Fact]
    public async Task SeveralCharactersOnOneServer_EachKeepTheirOwnRotatedPair()
    {
        var ct = TestContext.Current.CancellationToken;
        const int other = 90382598;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"), Tokens("access-9", "refresh-9", other));
        var call = RefreshCall.Rotating();
        var refresher = new ServerSessionRefresher(call, store);

        var results = await Task.WhenAll(
            refresher.TryRefreshAsync(Server, Character, "access-1", ct),
            refresher.TryRefreshAsync(Server, other, "access-9", ct));

        Assert.All(results, r => Assert.Equal(ServerSessionRefreshOutcome.Refreshed, r.Outcome));
        Assert.Equal(2, call.Calls); // neither was mistaken for the other's already-rotated session

        var mine = await store.LoadForCharacterAsync(Server, Character, ct);
        var theirs = await store.LoadForCharacterAsync(Server, other, ct);
        Assert.NotEqual(mine!.RefreshToken, theirs!.RefreshToken);
        Assert.Equal(Character, mine.CharacterId);
        Assert.Equal(other, theirs.CharacterId);
    }

    [Fact]
    public async Task NothingStoredToRefreshWith_IsUnavailable_NotARejection()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore();
        var refresher = new ServerSessionRefresher(RefreshCall.AlwaysRefuses(), store);

        var (outcome, _) = await refresher.TryRefreshAsync(Server, Character, cancellationToken: ct);

        // A missing session used to return Rejected, which the connect loop answered by deleting the session it had
        // just failed to find. Nothing to refresh is nothing to judge.
        Assert.Equal(ServerSessionRefreshOutcome.Unavailable, outcome);
    }

    [Fact]
    public async Task TheConvenienceOverload_GivesNothingBackOnARejection_ButStillKeepsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"));
        var refresher = new ServerSessionRefresher(RefreshCall.AlwaysRefuses(), store);

        // What FleetClient / ServerFitShareClient / ServerRunSyncClient see: null means "surface your own 401",
        // never "the pairing is finished".
        Assert.Null(await refresher.RefreshAsync(Server, Character, ct));
        Assert.Equal(0, store.RemoveCalls);
    }

    /// <summary>
    /// The refusal the server can now be definite about (ET-123): the session is not there any more. It has to come
    /// back as its own outcome, because the callers answer the two in opposite ways — one keeps retrying quietly,
    /// the other stops and puts the user in front of a "couple again". What must NOT differ is the credentials:
    /// they stay on disk either way, because coupling again is what replaces them and deleting them here would put
    /// back the silent unpairing ET-121 removed.
    /// </summary>
    [Fact]
    public async Task AServerSayingTheSessionIsGone_IsItsOwnOutcome_AndStillLeavesTheStoredPairingAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"));
        var refresher = new ServerSessionRefresher(RefreshCall.SaysSessionGone(), store);

        var (outcome, session) = await refresher.TryRefreshAsync(Server, Character, cancellationToken: ct);

        Assert.Equal(ServerSessionRefreshOutcome.SessionGone, outcome);
        Assert.Equal(0, store.RemoveCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal("refresh-1", session!.RefreshToken);
    }

    /// <summary>A refusal the server would not commit to stays exactly what it was before ET-123 — the whole point
    /// of putting the reason on the wire was to stop guessing, not to start treating every refusal as final.</summary>
    [Fact]
    public async Task ARefusalThatDoesNotSayTheSessionIsGone_IsStillTheRetryableOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSessionStore(Tokens("access-1", "refresh-1"));
        var refresher = new ServerSessionRefresher(RefreshCall.AlwaysRefuses(), store);

        var (outcome, _) = await refresher.TryRefreshAsync(Server, Character, cancellationToken: ct);

        Assert.Equal(ServerSessionRefreshOutcome.Rejected, outcome);
        Assert.Equal(0, store.RemoveCalls);
    }

    private static ClientSessionTokens Tokens(string access, string refresh, int characterId = Character) =>
        new(access, refresh, "Jithran", characterId);

    /// <summary>A <see cref="IServerSessionRefreshCall"/> with the three answers that matter, and a call count.</summary>
    private sealed class RefreshCall : IServerSessionRefreshCall
    {
        private readonly Func<string, ServerSessionRefreshReply> _reply;
        private int _calls;

        private RefreshCall(Func<string, ServerSessionRefreshReply> reply) => _reply = reply;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Refuses without saying the session is gone — the ambiguous refusal ET-121's rules are about.</summary>
        public static RefreshCall AlwaysRefuses() => new(_ => new ServerSessionRefreshReply(false, "", ""));

        /// <summary>Refuses and says outright that the session no longer exists (ET-123).</summary>
        public static RefreshCall SaysSessionGone() =>
            new(_ => new ServerSessionRefreshReply(false, "", "", SessionGone: true));

        public static RefreshCall Throws() => new(_ => throw new InvalidOperationException("server unreachable"));

        /// <summary>Rotates like the server does: every accepted refresh token becomes a new pair.</summary>
        public static RefreshCall Rotating()
        {
            var generation = 1;
            return new RefreshCall(_ =>
            {
                var next = Interlocked.Increment(ref generation);
                return new ServerSessionRefreshReply(true, $"access-{next}", $"refresh-{next}");
            });
        }

        public async Task<ServerSessionRefreshReply> RefreshAsync(
            string serverAddress, string refreshToken, int sessionId = 0, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.Yield(); // give a racing caller room to arrive while this one is "in flight"
            return _reply(refreshToken);
        }
    }

    /// <summary>An in-memory <see cref="IClientSessionStore"/> that counts the two calls this ticket is about.</summary>
    private sealed class RecordingSessionStore : IClientSessionStore
    {
        private readonly ConcurrentDictionary<int, ClientSessionTokens> _sessions = new();
        private int _saveCalls;
        private int _removeCalls;

        public RecordingSessionStore(params ClientSessionTokens[] seed)
        {
            foreach (var tokens in seed)
                _sessions[tokens.CharacterId] = tokens;
        }

        public int SaveCalls => Volatile.Read(ref _saveCalls);
        public int RemoveCalls => Volatile.Read(ref _removeCalls);

        public Task SaveAsync(string serverAddress, ClientSessionTokens tokens, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCalls);
            _sessions[tokens.CharacterId] = tokens;
            return Task.CompletedTask;
        }

        public Task<ClientSessionTokens?> LoadAsync(string serverAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions.Values.FirstOrDefault());

        public Task<ClientSessionTokens?> LoadForCharacterAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions.TryGetValue(characterId, out var tokens) ? tokens : null);

        public Task<IReadOnlyList<ClientSessionTokens>> LoadAllAsync(string serverAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientSessionTokens>>(_sessions.Values.ToList());

        public Task SetServerSessionIdAsync(string serverAddress, int characterId, int serverSessionId, CancellationToken cancellationToken = default)
        {
            if (serverSessionId > 0 && _sessions.TryGetValue(characterId, out var tokens))
                _sessions[characterId] = tokens with { ServerSessionId = serverSessionId };
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _removeCalls);
            _sessions.TryRemove(characterId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListServersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([Server]);

        public Task<IReadOnlyList<string>> ListServersForCharacterAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([Server]);
    }
}
