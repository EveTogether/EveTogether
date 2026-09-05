using EveUtils.Server.Esi;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EveUtils.Server.Tests;

public sealed class ServerTokenRefreshServiceTests
{
    [Fact]
    public async Task RefreshAllAsync_RevokedToken_DoesNotRetry()
    {
        using var harness = await TokenRefreshHarness.CreateAsync(new InvalidOperationException("invalid_grant"));

        await harness.Service.RefreshAllAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.Service.RefreshAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.AuthClient.RefreshCalls);
    }

    [Fact]
    public async Task RefreshAllAsync_TransientFailure_DoesNotRefreshEveryCycle()
    {
        using var harness = await TokenRefreshHarness.CreateAsync(new HttpRequestException("network unavailable"));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await harness.Service.RefreshAllAsync(TestContext.Current.CancellationToken);
            harness.Time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.True(harness.AuthClient.RefreshCalls < 3);
    }

    [Fact]
    public async Task RefreshAllAsync_PersistentFailure_LogsLessThanEveryCycle()
    {
        using var harness = await TokenRefreshHarness.CreateAsync(new HttpRequestException("network unavailable"));

        for (var cycle = 0; cycle < 10; cycle++)
        {
            await harness.Service.RefreshAllAsync(TestContext.Current.CancellationToken);
            harness.Time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.True(harness.Logger.ErrorCount < 10);
    }

    private sealed class TokenRefreshHarness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SqliteServerDbContextFactory _factory;

        private TokenRefreshHarness(
            ServiceProvider services,
            SqliteServerDbContextFactory factory,
            ServerTokenRefreshService service,
            TestTimeProvider time,
            FailingEsiAuthClient authClient,
            CapturingLogger logger)
        {
            _services = services;
            _factory = factory;
            Service = service;
            Time = time;
            AuthClient = authClient;
            Logger = logger;
        }

        public ServerTokenRefreshService Service { get; }
        public TestTimeProvider Time { get; }
        public FailingEsiAuthClient AuthClient { get; }
        public CapturingLogger Logger { get; }

        public static async Task<TokenRefreshHarness> CreateAsync(Exception exception)
        {
            var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
            var factory = new SqliteServerDbContextFactory();
            await using (SharedDbContext db = factory.CreateDbContext())
            {
                db.Set<SyncedCharacter>().Add(new SyncedCharacter
                {
                    EsiCharacterId = 1,
                    CharacterName = "Test character",
                    RefreshTokenCipher = [1],
                    RefreshTokenNonce = [2],
                    RefreshTokenTag = [3],
                    PairedAt = time.GetUtcNow(),
                    LastRefreshedAt = time.GetUtcNow() - TimeSpan.FromMinutes(16)
                });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var repository = new ServerAuthRepository(factory);
            var services = new ServiceCollection()
                .AddSingleton<IServerAuthRepository>(repository)
                .AddSingleton<ITokenProtector, EmptyTokenProtector>()
                .BuildServiceProvider();
            var authClient = new FailingEsiAuthClient(exception);
            var logger = new CapturingLogger();
            var service = new ServerTokenRefreshService(
                services.GetRequiredService<IServiceScopeFactory>(),
                authClient,
                new UnusedJwtValidator(),
                new EsiOptions(),
                time,
                logger);
            return new TokenRefreshHarness(services, factory, service, time, authClient, logger);
        }

        public void Dispose()
        {
            _services.Dispose();
            _factory.Dispose();
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FailingEsiAuthClient(Exception exception) : IEsiAuthClient
    {
        public int RefreshCalls { get; private set; }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromException<EsiTokenSet>(exception);

        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) =>
            Task.FromException<EsiTokenSet>(exception);

        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) =>
            Task.FromException<EsiTokenSet>(exception);

        public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromException<EsiTokenSet>(exception);
        }
    }

    private sealed class UnusedJwtValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromException<EsiIdentity>(new InvalidOperationException());
    }

    private sealed class EmptyTokenProtector : ITokenProtector
    {
        public EncryptedToken Protect(string plaintext) => new([], [], []);

        public string Unprotect(EncryptedToken token) => string.Empty;
    }

    private sealed class CapturingLogger : ILogger<ServerTokenRefreshService>
    {
        public int ErrorCount { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                ErrorCount++;
        }

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
