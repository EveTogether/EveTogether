using System.Security.Cryptography;
using System.Text;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// A client can only be told a refused refresh means "this session is gone" while it can name the session it holds,
/// and the id it names with is the one thing about a session that survives a rotation (ET-123). A client that has
/// just paired otherwise has no id at all until a heartbeat hands it one, which measured at ~30s — a window in which
/// the very refusal this is for degrades back to a plain retry. That matters most right where it is least welcome:
/// coupling again is the way out of "your session is gone", so the recovery would start by being unclassifiable all
/// over again.
///
/// So the pairing hands the id over with the tokens. This drives the real claim and then asks the real refusal rule
/// about the session it named, because handing over an id that does not identify anything would look perfectly fine
/// on its own.
/// </summary>
public class PairingHandsOverTheSessionIdTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task ClaimingAPairing_NamesTheSessionItIssued_AndThatNameIsWhatTheRefusalRuleAnswersOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90382598, "Abnoba Auscent", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);

        // What PairingCompleter leaves behind once the SSO round-trip is done.
        var issued = await sessions.IssueAsync(character.Id, ct);
        const string secret = "pairing-secret";
        var store = new PairingStateStore();
        store.Add(new PairingState
        {
            PairingId = "pairing-1",
            PairingChallenge = Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(secret))),
            OAuthState = "oauth-state",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = PairingStatus.Completed,
            CharacterId = 90382598,
            CharacterName = "Abnoba Auscent",
            SessionToken = issued.AccessToken,
            SessionRefreshToken = issued.RefreshToken,
            SessionId = issued.SessionId,
        });

        var service = new PairingService(null!, new ServerInfo("Test"), null!, store, null!);
        var claim = await service.ClaimPairing(
            new ClaimPairingRequest { PairingId = "pairing-1", PairingSecret = secret }, new NoContext());

        Assert.True(claim.Completed);
        Assert.Equal(issued.SessionId, claim.SessionId);

        // And the id is worth having: the moment that session is deleted, naming it is what turns a refusal from
        // "keep retrying" into "couple again". Handing over a number that identifies nothing would pass the
        // assertion above and still leave the client stuck on the fallback.
        await repository.DeleteSessionAsync(claim.SessionId, ct);
        var refused = await sessions.RefreshAsync(
            issued.RefreshToken, claimedSessionId: claim.SessionId, cancellationToken: ct);
        Assert.Equal(SessionRefusalReason.SessionGone, refused.Refusal);
    }

    /// <summary>Nothing in the claim path reads the call context, so it is left unimplemented rather than pulling in
    /// Grpc.Core.Testing for one factory call — the same trade the unauthenticated-status tests make.</summary>
    private sealed class NoContext : ServerCallContext
    {
        protected override Metadata RequestHeadersCore { get; } = [];
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
