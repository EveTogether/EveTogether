using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// A coupling started for one character must not end up coupling whoever was picked on the EVE page (ET-425). The
/// client names the character when it starts the pairing; the server is where the signed token is read, so it is the
/// server that refuses — before it stores a refresh token or issues a session.
/// </summary>
public sealed class PairingRefusesAnotherCharacterTests : IDisposable
{
    private const int ExpectedCharacterId = 2119384756;
    private const int PickedCharacterId = 2122093318;

    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task CompleteAsync_AnotherCharacterSignsIn_FailsAsOtherCharacterAndStoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var completer = new PairingCompleter(
            new EsiOptions { ClientId = "app-id", ClientSecret = "app-secret" },
            new FixedAuthClient(),
            new FixedJwtValidator(new EsiIdentity(PickedCharacterId, "Ysel Marrow", ["publicData"])),
            new UnusedAffiliationResolver(),
            repository,
            new UnusedProtector(),
            new UnusedToggles(),
            new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance));
        var state = new PairingState
        {
            PairingId = "pairing-1",
            PairingChallenge = "challenge",
            OAuthState = "oauth-state",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpectedCharacterId = ExpectedCharacterId,
        };

        var (ok, _) = await completer.CompleteAsync(state, "code", "oauth-state", ct);

        Assert.False(ok);
        Assert.Equal(PairingStatus.Failed, state.Status);
        Assert.Equal(PairingFailure.OtherCharacter, state.Failure);
        Assert.Equal("Ysel Marrow", state.CharacterName);
        Assert.Null(state.SessionToken);
        Assert.Empty(await repository.ListSyncedAsync(ct));
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FixedAuthClient : IEsiAuthClient
    {
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EsiTokenSet("access-token", "refresh-token", DateTimeOffset.UtcNow.AddMinutes(20)));

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedJwtValidator(EsiIdentity identity) : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(identity);
    }

    private sealed class UnusedAffiliationResolver : IEsiAffiliationResolver
    {
        public Task<EsiCharacterAffiliation?> ResolveAsync(int characterId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ResolveCorporationNameAsync(int corporationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ResolveAllianceNameAsync(int allianceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedProtector : ITokenProtector
    {
        public EncryptedToken Protect(string plaintext) => throw new NotSupportedException();
        public string Unprotect(EncryptedToken token) => throw new NotSupportedException();
    }

    private sealed class UnusedToggles : IPermissionToggleStore
    {
        public bool IsEnabled(string code) => throw new NotSupportedException();
        public void SetEnabled(string code, bool value) => throw new NotSupportedException();
    }
}
