using System.Net;
using EveUtils.Server.Api;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Repositories.Implementations;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-142: the lock on the realtime channel. Driven over a real SignalR connection to a real Kestrel, because both
/// promises are about the connection and not about a method: one is refused before it is open, the other is broken
/// after it was. The hub pushes nothing — what it would push waits for a consumer.
/// </summary>
public class ServerApiRealtimeTests : IAsyncLifetime
{
    /// <summary>How a caller can arrive at the hub without a key that should let it in.</summary>
    public enum BadKey { Absent, Unknown, Revoked, Expired }

    /// <summary>How an open connection stops being provably valid — the last being the watch itself failing,
    /// which has to close the connection rather than leave it unwatched.</summary>
    public enum KeyFate { Revoked, Expired, RecheckFails }

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly FlakyApiKeyRepository _keys;
    private WebApplication _app = null!;
    private string _url = string.Empty;

    private string _liveKey = string.Empty;
    private int _liveKeyId;
    private string _revokedKey = string.Empty;
    private string _expiredKey = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ServerApiRealtimeTests() => _keys = new FlakyApiKeyRepository(new ApiKeyRepository(_factory));

    [Theory]
    [InlineData(BadKey.Absent)]
    [InlineData(BadKey.Unknown)]
    [InlineData(BadKey.Revoked)]
    [InlineData(BadKey.Expired)]
    public async Task AConnectionWithoutALivingKey_IsRefusedBeforeItIsOpen(BadKey presented)
    {
        await using HubConnection connection = _Connect(presented switch
        {
            BadKey.Absent => null,
            BadKey.Unknown => ApiKeySecurity.Generate().PlainText, // well-formed, never stored
            BadKey.Revoked => _revokedKey,
            _ => _expiredKey
        });

        HttpRequestException refused =
            await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));

        // The negotiate is refused, so the connection never opens — there is no socket to send anything over.
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    /// <summary>The counter-proof: without this, a hub that refuses everyone would pass the four cases above.</summary>
    [Fact]
    public async Task ALivingKey_Connects()
    {
        await using HubConnection connection = _Connect(_liveKey);

        await connection.StartAsync(Ct);

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Theory]
    [InlineData(KeyFate.Revoked)]
    [InlineData(KeyFate.Expired)]
    [InlineData(KeyFate.RecheckFails)]
    public async Task AnOpenConnectionWhoseKeyStopsBeingValid_IsClosed(KeyFate fate)
    {
        await using HubConnection connection = _Connect(_liveKey);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync(Ct);

        await _EndTheKeyAsync(fate);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    // --- Host and fixtures ---

    public async ValueTask InitializeAsync()
    {
        (_liveKey, _liveKeyId) = await _StoreKeyAsync("live");
        (_revokedKey, _) = await _StoreKeyAsync("revoked", isActive: false);
        (_expiredKey, _) = await _StoreKeyAsync("expired", expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // 200 ms instead of the 30-second default: the whole point of the knob is that a test need not wait.
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ServerApi:RealtimeKeyRecheck"] = "00:00:00.200" });

        builder.Services.AddSingleton<IApiKeyRepository>(_keys);
        builder.Services.AddSignalR();
        builder.Services.AddAuthentication(ApiKeyAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthentication.Scheme, null);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(ApiKeyAuthentication.Policy, ApiKeyAuthentication.BuildPolicy()));
        builder.Services.AddServerApiDocs();
        ServerApiOptions hardening = builder.AddServerApiHardening();

        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0"); // port 0: the OS hands out a free one
        _app.UseServerApiHardening(hardening);
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapServerApi();

        await _app.StartAsync(Ct);
        _url = _app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _factory.Dispose();
    }

    /// <summary>The key rides in the query string — the form a browser's WebSocket is left with, since it cannot
    /// put a header on the handshake.</summary>
    private HubConnection _Connect(string? key) => new HubConnectionBuilder()
        .WithUrl(key is null
            ? $"{_url}/api/v1/realtime"
            : $"{_url}/api/v1/realtime?{ApiKeyAuthentication.QueryName}={Uri.EscapeDataString(key)}")
        .Build();

    private async Task _EndTheKeyAsync(KeyFate fate)
    {
        switch (fate)
        {
            case KeyFate.Revoked:
                await _keys.SetActiveAsync(_liveKeyId, isActive: false, Ct);
                break;
            case KeyFate.Expired:
                await using (SharedDbContext db = _factory.CreateDbContext())
                {
                    ApiKey key = await db.Set<ApiKey>().SingleAsync(k => k.Id == _liveKeyId, Ct);
                    key.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
                    await db.SaveChangesAsync(Ct);
                }

                break;
            default:
                _keys.ThrowOnLookup = true;
                break;
        }
    }

    private async Task<(string PlainText, int Id)> _StoreKeyAsync(
        string label, bool isActive = true, DateTimeOffset? expiresAt = null)
    {
        GeneratedApiKey generated = ApiKeySecurity.Generate();
        int id = await _keys.AddAsync(new ApiKey
        {
            Label = label,
            Prefix = generated.Prefix,
            SecretHash = ApiKeySecurity.Hash(generated.Secret),
            Scopes = ApiKeyScopes.ReadAll,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "tests",
            ExpiresAt = expiresAt
        }, Ct);
        return (generated.PlainText, id);
    }

    /// <summary>The real repository with a switch on the one read the hub's watch makes, so a failing lookup can be
    /// staged the way a database outage would arrive.</summary>
    private sealed class FlakyApiKeyRepository(IApiKeyRepository inner) : IApiKeyRepository
    {
        public bool ThrowOnLookup { get; set; }

        public Task<ApiKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            ThrowOnLookup
                ? throw new InvalidOperationException("The key store is unreachable.")
                : inner.FindByPrefixAsync(prefix, cancellationToken);

        public Task<int> AddAsync(ApiKey key, CancellationToken cancellationToken = default) =>
            inner.AddAsync(key, cancellationToken);

        public Task<ApiKey?> GetAsync(int id, CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken);

        public Task<bool> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default) =>
            inner.SetActiveAsync(id, isActive, cancellationToken);

        public Task<bool> SetScopesAsync(int id, string scopes, CancellationToken cancellationToken = default) =>
            inner.SetScopesAsync(id, scopes, cancellationToken);

        public Task TouchLastUsedAsync(int id, DateTimeOffset usedAt, CancellationToken cancellationToken = default) =>
            inner.TouchLastUsedAsync(id, usedAt, cancellationToken);

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);
    }
}
