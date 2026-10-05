using EveUtils.Server.Auth;
using EveUtils.Server.Esi;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-448: the on-demand token provider and the background refresh used to refresh one character independently. EVE
/// rotates refresh tokens, so when both ran at once the second offered a token the first had just spent, got
/// invalid_grant, and the grant was gone. Both now go through one gate per character.
/// </summary>
public sealed class ServerTokenRefreshGateTests : IDisposable
{
    private const int EsiCharacterId = 90250177;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServerAuthRepository _repository;
    private readonly ServiceProvider _services;
    private readonly ServerTokenRefreshGate _gate = new();
    private readonly RotatingSso _sso = new();

    public ServerTokenRefreshGateTests()
    {
        _repository = new ServerAuthRepository(_factory);
        _services = new ServiceCollection()
            .AddSingleton<IServerAuthRepository>(_repository)
            .AddSingleton<ITokenProtector, PlainTokenProtector>()
            .AddSingleton<IEsiTokenRevoker, NoRevoker>()
            .AddSingleton(new EsiOptions { ClientId = "app", ClientSecret = "secret" })
            .AddLogging()
            .AddSingleton<SyncedCharacterReleaser>()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task BackgroundRefreshAndAnEsiCallAtOnce_NeverOfferTheSameRefreshTokenTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await _SeedAsync(ct);
        _sso.HoldFirstCall = true;

        var background = _NewRefreshService().RefreshAllAsync(ct);
        await _sso.FirstCallEntered.WaitAsync(ct);
        var esiCall = _NewProvider().AuthorizeAsync(EsiCharacterId, [], ct);
        await Task.WhenAny(esiCall, Task.Delay(TimeSpan.FromMilliseconds(200), ct)); // without the gate it is done by now
        _sso.ReleaseFirstCall();
        await background;
        var authorization = await esiCall;

        Assert.Empty(_sso.Reused);
        Assert.Equal(EsiAuthOutcome.Authorized, authorization.Outcome);
        Assert.Equal(["original", "rt-1"], _sso.Offered); // the ESI call read the token the background refresh stored
        Assert.Equal("rt-2", _StoredRefreshToken(await _repository.FindSyncedAsync(EsiCharacterId, ct)));
    }

    [Fact]
    public async Task TwoEsiCallsAtOnce_RefreshOnce_AndShareTheToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await _SeedAsync(ct);
        _sso.HoldFirstCall = true;
        var provider = _NewProvider();

        var first = provider.AuthorizeAsync(EsiCharacterId, [], ct);
        await _sso.FirstCallEntered.WaitAsync(ct);
        var second = provider.AuthorizeAsync(EsiCharacterId, [], ct);
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200), ct));
        _sso.ReleaseFirstCall();
        var both = await Task.WhenAll(first, second);

        Assert.Empty(_sso.Reused);
        Assert.Equal(["original"], _sso.Offered);
        Assert.All(both, authorization => Assert.Equal("access-1", authorization.AccessToken));
    }

    private ServerTokenRefreshService _NewRefreshService() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _sso, new FixedJwtValidator(),
            new EsiOptions { ClientId = "app", ClientSecret = "secret" }, _gate, TimeProvider.System,
            NullLogger<ServerTokenRefreshService>.Instance);

    private ServerEsiTokenProvider _NewProvider() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _sso,
            new EsiOptions { ClientId = "app", ClientSecret = "secret" }, _gate,
            NullLogger<ServerEsiTokenProvider>.Instance);

    private async Task _SeedAsync(CancellationToken ct)
    {
        var token = new PlainTokenProtector().Protect("original");
        await using var db = _factory.CreateDbContext();
        var synced = new SyncedCharacter
        {
            EsiCharacterId = EsiCharacterId,
            CharacterName = "Jithran",
            RefreshTokenCipher = token.Cipher,
            RefreshTokenNonce = token.Nonce,
            RefreshTokenTag = token.Tag,
            GrantedScopes = ["esi-location.read_location.v1"],
            PairedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(16)
        };
        db.Set<SyncedCharacter>().Add(synced);
        await db.SaveChangesAsync(ct);
        db.Set<ServerSession>().Add(new ServerSession { SyncedCharacterId = synced.Id, AccessTokenHash = "access", RefreshTokenHash = "refresh" });
        await db.SaveChangesAsync(ct);
    }

    private static string? _StoredRefreshToken(SyncedCharacter? stored) => stored is null
        ? null
        : new PlainTokenProtector().Unprotect(new EncryptedToken(stored.RefreshTokenCipher, stored.RefreshTokenNonce, stored.RefreshTokenTag));

    public void Dispose()
    {
        _services.Dispose();
        _factory.Dispose();
    }

    /// <summary>EVE SSO with rotation: each refresh token works once and is replaced by the next.</summary>
    private sealed class RotatingSso : IEsiAuthClient
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _valid = ["original"];
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _issued;

        public bool HoldFirstCall { get; set; }
        public List<string> Offered { get; } = [];
        public List<string> Reused { get; } = [];
        public Task FirstCallEntered => _firstEntered.Task;

        public void ReleaseFirstCall() => _releaseFirst.TrySetResult();

        public async Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            bool isFirst;
            int issued;
            lock (_lock)
            {
                Offered.Add(refreshToken);
                isFirst = Offered.Count == 1;
                if (!_valid.Remove(refreshToken))
                {
                    Reused.Add(refreshToken);
                    throw new EsiTokenExchangeException(400,
                        """{"error":"invalid_grant","error_description":"Invalid refresh token. Character grant missing/expired."}""");
                }
                issued = ++_issued;
                _valid.Add($"rt-{issued}");
            }

            if (isFirst && HoldFirstCall)
            {
                _firstEntered.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return new EsiTokenSet($"access-{issued}", $"rt-{issued}", DateTimeOffset.UtcNow.AddMinutes(20));
        }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedJwtValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EsiIdentity(EsiCharacterId, "Jithran", ["esi-location.read_location.v1"]));
    }

    private sealed class NoRevoker : IEsiTokenRevoker
    {
        public Task RevokeRefreshTokenAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class PlainTokenProtector : ITokenProtector
    {
        public EncryptedToken Protect(string plaintext) => new(System.Text.Encoding.UTF8.GetBytes(plaintext), [1], [2]);

        public string Unprotect(EncryptedToken token) => System.Text.Encoding.UTF8.GetString(token.Cipher);
    }
}
