using EveUtils.Server.Auth;
using EveUtils.Server.Esi;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-344: a character nobody is coupled to any more must not have its EVE token kept fresh, and a refresh that
/// overlaps its release must not bring the deleted row back.
/// </summary>
public sealed class ServerTokenRefreshReleasedCharacterTests : IDisposable
{
    private const int EsiCharacterId = 90382598;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServerAuthRepository _repository;
    private readonly RecordingRevoker _revoker = new();
    private readonly ServiceProvider _services;

    public ServerTokenRefreshReleasedCharacterTests()
    {
        _repository = new ServerAuthRepository(_factory);
        _services = new ServiceCollection()
            .AddSingleton<IServerAuthRepository>(_repository)
            .AddSingleton<ITokenProtector, PlainTokenProtector>()
            .AddSingleton<IEsiTokenRevoker>(_revoker)
            .AddSingleton(new EsiOptions { ClientId = "app", ClientSecret = "secret" })
            .AddLogging()
            .AddSingleton<SyncedCharacterReleaser>()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task RefreshAllAsync_CharacterWithoutSession_IsNotRefreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _SeedAsync(withSession: false, ct);
        var authClient = new RotatingAuthClient();

        await _NewService(authClient).RefreshAllAsync(ct);

        Assert.Equal(0, authClient.RefreshCalls);
    }

    [Fact]
    public async Task RefreshAllAsync_CharacterWithSession_StoresTheRefreshedToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await _SeedAsync(withSession: true, ct);

        await _NewService(new RotatingAuthClient()).RefreshAllAsync(ct);

        var stored = Assert.Single(await _repository.ListSyncedAsync(ct));
        Assert.Equal("rotated", new PlainTokenProtector().Unprotect(new EncryptedToken(stored.RefreshTokenCipher, stored.RefreshTokenNonce, stored.RefreshTokenTag)));
        Assert.Equal(["esi-skills.read_skills.v1"], stored.GrantedScopes);
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task RefreshAllAsync_CharacterReleasedWhileRefreshing_IsNotWrittenBack_AndTheNewTokenIsRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        var synced = await _SeedAsync(withSession: true, ct);
        var authClient = new RotatingAuthClient
        {
            WhileRefreshing = async () =>
            {
                foreach (var session in await _repository.ListSessionsAsync(ct))
                    await _repository.DeleteSessionAsync(session.Id, ct);
                await _repository.DeleteSyncedIfWithoutSessionAsync(synced.Id, ct);
            }
        };

        await _NewService(authClient).RefreshAllAsync(ct);

        Assert.Empty(await _repository.ListSyncedAsync(ct));
        Assert.Equal(["rotated"], _revoker.Revoked);
    }

    private ServerTokenRefreshService _NewService(IEsiAuthClient authClient) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), authClient, new FixedJwtValidator(),
            new EsiOptions { ClientId = "app", ClientSecret = "secret" }, TimeProvider.System,
            NullLogger<ServerTokenRefreshService>.Instance);

    private async Task<SyncedCharacter> _SeedAsync(bool withSession, CancellationToken ct)
    {
        var token = new PlainTokenProtector().Protect("original");
        await using var db = _factory.CreateDbContext();
        var synced = new SyncedCharacter
        {
            EsiCharacterId = EsiCharacterId,
            CharacterName = "Abnoba Auscent",
            RefreshTokenCipher = token.Cipher,
            RefreshTokenNonce = token.Nonce,
            RefreshTokenTag = token.Tag,
            GrantedScopes = ["esi-skills.read_skills.v1"],
            PairedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(16)
        };
        db.Set<SyncedCharacter>().Add(synced);
        await db.SaveChangesAsync(ct);

        if (withSession)
        {
            db.Set<ServerSession>().Add(new ServerSession { SyncedCharacterId = synced.Id, AccessTokenHash = "access", RefreshTokenHash = "refresh" });
            await db.SaveChangesAsync(ct);
        }

        return synced;
    }

    public void Dispose()
    {
        _services.Dispose();
        _factory.Dispose();
    }

    private sealed class RotatingAuthClient : IEsiAuthClient
    {
        public int RefreshCalls { get; private set; }
        public Func<Task>? WhileRefreshing { get; init; }

        public async Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            if (WhileRefreshing is not null)
                await WhileRefreshing();
            return new EsiTokenSet("access-token", "rotated", DateTimeOffset.UtcNow.AddMinutes(20));
        }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedJwtValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EsiIdentity(EsiCharacterId, "Abnoba Auscent", ["esi-skills.read_skills.v1"]));
    }

    private sealed class RecordingRevoker : IEsiTokenRevoker
    {
        public List<string> Revoked { get; } = [];

        public Task RevokeRefreshTokenAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            Revoked.Add(refreshToken);
            return Task.CompletedTask;
        }
    }

    private sealed class PlainTokenProtector : ITokenProtector
    {
        public EncryptedToken Protect(string plaintext) => new(System.Text.Encoding.UTF8.GetBytes(plaintext), [1], [2]);

        public string Unprotect(EncryptedToken token) => System.Text.Encoding.UTF8.GetString(token.Cipher);
    }
}
