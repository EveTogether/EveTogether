using System.Security.Claims;
using System.Text.Encodings.Web;
using EveUtils.Server.Api;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Repositories.Implementations;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-118: the lock on <c>/api/v1</c>. Every way of presenting a key that should not open the door is refused,
/// and a refused authentication is what the pipeline turns into a 401 — so these cases are the five 401s the
/// acceptance criteria ask for. The scope gate is checked against the same policy object the host builds,
/// because that is the difference between a 403 and a 401.
/// </summary>
public class ApiKeyAuthenticationTests : IDisposable
{
    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly IApiKeyRepository _repository;
    private readonly CapturingLoggerProvider _logs = new();

    public ApiKeyAuthenticationTests() => _repository = new ApiKeyRepository(_factory);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AValidKeyInTheHeader_Authenticates()
    {
        var (plainText, _) = await _StoreKeyAsync();

        AuthenticateResult result = await _AuthenticateAsync(header: plainText);

        Assert.True(result.Succeeded);
        Assert.Equal("dashboard", result.Principal?.Identity?.Name);
    }

    [Fact]
    public async Task AValidKeyInTheQuery_AuthenticatesToo()
    {
        var (plainText, _) = await _StoreKeyAsync();

        AuthenticateResult result = await _AuthenticateAsync(query: plainText);

        Assert.True(result.Succeeded);
    }

    /// <summary>The query form is a proxy favourite to log, so ours never writes it anywhere itself.</summary>
    [Fact]
    public async Task AKeyInTheQuery_IsNeverWrittenToTheLog()
    {
        var (plainText, key) = await _StoreKeyAsync();

        await _AuthenticateAsync(query: plainText);

        Assert.DoesNotContain(_logs.Lines, line => line.Contains(plainText, StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains(key.SecretHash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoKeyAtAll_IsRefused()
    {
        await _StoreKeyAsync();

        AuthenticateResult result = await _AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AnUnknownPrefix_IsRefused()
    {
        await _StoreKeyAsync();

        AuthenticateResult result = await _AuthenticateAsync(
            header: "evek_00000000_" + ApiKeySecurity.Generate().Secret);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AKnownPrefixWithTheWrongSecret_IsRefused()
    {
        var (_, key) = await _StoreKeyAsync();

        AuthenticateResult result = await _AuthenticateAsync(
            header: $"evek_{key.Prefix}_{ApiKeySecurity.Generate().Secret}");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ARevokedKey_IsRefused()
    {
        var (plainText, key) = await _StoreKeyAsync();
        await _repository.SetActiveAsync(key.Id, isActive: false, TestContext.Current.CancellationToken);

        AuthenticateResult result = await _AuthenticateAsync(header: plainText);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AnExpiredKey_IsRefused()
    {
        var (plainText, _) = await _StoreKeyAsync(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        AuthenticateResult result = await _AuthenticateAsync(header: plainText);

        Assert.False(result.Succeeded);
    }

    /// <summary>A refusal says the same thing whatever went wrong: which stage failed is not the caller business.</summary>
    [Fact]
    public async Task ARefusal_TellsTheCallerNothingAboutTheKey()
    {
        var (_, key) = await _StoreKeyAsync();

        AuthenticateResult wrongSecret = await _AuthenticateAsync(
            header: $"evek_{key.Prefix}_{ApiKeySecurity.Generate().Secret}");
        AuthenticateResult unknownPrefix = await _AuthenticateAsync(header: "evek_00000000_nope");

        Assert.Equal("Invalid API key.", wrongSecret.Failure?.Message);
        Assert.Equal(wrongSecret.Failure?.Message, unknownPrefix.Failure?.Message);
    }

    [Fact]
    public async Task AUsedKey_IsStampedWithItsLastUse()
    {
        var (plainText, key) = await _StoreKeyAsync();
        Assert.Null(key.LastUsedAt);

        await _AuthenticateAsync(header: plainText);

        ApiKey? stored = await _repository.GetAsync(key.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(stored?.LastUsedAt);
    }

    [Fact]
    public async Task AKeyWithTheReadScope_PassesTheApiPolicy()
    {
        var (plainText, _) = await _StoreKeyAsync();

        AuthenticateResult result = await _AuthenticateAsync(header: plainText);

        Assert.True(await _IsAuthorizedAsync(result.Principal));
    }

    /// <summary>Authentication succeeds and authorization does not — the split that makes this a 403, not a 401.</summary>
    [Fact]
    public async Task AKeyWithoutTheReadScope_AuthenticatesButFailsThePolicy()
    {
        var (plainText, _) = await _StoreKeyAsync(scopes: string.Empty);

        AuthenticateResult result = await _AuthenticateAsync(header: plainText);

        Assert.True(result.Succeeded);
        Assert.False(await _IsAuthorizedAsync(result.Principal));
    }

    /// <summary>
    /// The endpoint that proves the lock answers out of the key that opened it, and out of nothing else — no
    /// plaintext, no hash. An unowned key reports the admin scope over all server data as an absent owner.
    /// </summary>
    [Fact]
    public async Task WhoAmI_AnswersFromTheKeyThatOpenedTheDoor_AndCarriesNoSecret()
    {
        var (plainText, key) = await _StoreKeyAsync(ownerCharacterId: 90_000_001);
        AuthenticateResult result = await _AuthenticateAsync(header: plainText);

        ApiWhoAmIResponse whoAmI = ApiWhoAmIResponse.From(
            result.Principal ?? throw new InvalidOperationException("the key did not authenticate"));

        Assert.Equal(key.Prefix, whoAmI.Prefix);
        Assert.Equal("dashboard", whoAmI.Label);
        Assert.Equal([ApiKeyScopes.ReadAll], whoAmI.Scopes);
        Assert.Equal(90_000_001, whoAmI.OwnerCharacterId);
        Assert.DoesNotContain(key.SecretHash, whoAmI.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(plainText, whoAmI.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhoAmI_AKeyWithoutAnOwner_ReportsNoOwner()
    {
        var (plainText, _) = await _StoreKeyAsync();
        AuthenticateResult result = await _AuthenticateAsync(header: plainText);

        ApiWhoAmIResponse whoAmI = ApiWhoAmIResponse.From(
            result.Principal ?? throw new InvalidOperationException("the key did not authenticate"));

        Assert.Null(whoAmI.OwnerCharacterId);
    }

    /// <summary>
    /// The gate is on the group, not on one route: every endpoint the API maps carries the API-key policy and
    /// none of them opts back out. Dropping <c>RequireAuthorization</c> — or adding an <c>AllowAnonymous</c> —
    /// turns this red, which no test of the policy object on its own would catch.
    /// </summary>
    [Fact]
    public void EveryEndpointUnderApiV1_CarriesTheApiKeyPolicy()
    {
        IEndpointRouteBuilder endpoints = WebApplication.CreateBuilder().Build();
        endpoints.MapServerApi();

        List<RouteEndpoint> routes = [.. endpoints.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()];

        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.StartsWith("/api/v1/", route.RoutePattern.RawText, StringComparison.Ordinal);
            Assert.Contains(route.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                gate => gate.Policy == ApiKeyAuthentication.Policy);
            Assert.Empty(route.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        });
    }

    /// <summary>
    /// The panel page is gated on its own permission, and that code is in the list the host turns into policies —
    /// a code missing there leaves the page referring to a policy that was never registered.
    /// </summary>
    [Fact]
    public void TheApiKeysPage_IsGatedOnApiKeysManage()
    {
        // The panel applies a bare [Authorize] to every page from _Imports, so the page's own gate is the
        // one in this set that names a policy.
        var gates = typeof(Components.Pages.ApiKeys)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .ToList();

        Assert.Contains(PanelPermissions.ApiKeysManage, gates);
        Assert.Contains(PanelPermissions.ApiKeysManage, PanelPermissions.All);
    }

    private async Task<(string PlainText, ApiKey Key)> _StoreKeyAsync(
        string scopes = ApiKeyScopes.ReadAll, DateTimeOffset? expiresAt = null, int? ownerCharacterId = null)
    {
        GeneratedApiKey generated = ApiKeySecurity.Generate();
        var key = new ApiKey
        {
            Label = "dashboard",
            Prefix = generated.Prefix,
            SecretHash = ApiKeySecurity.Hash(generated.Secret),
            Scopes = scopes,
            OwnerCharacterId = ownerCharacterId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "tests",
            ExpiresAt = expiresAt
        };
        await _repository.AddAsync(key, TestContext.Current.CancellationToken);
        return (generated.PlainText, key);
    }

    private async Task<AuthenticateResult> _AuthenticateAsync(string? header = null, string? query = null)
    {
        var context = new DefaultHttpContext();
        if (header is not null)
            context.Request.Headers[ApiKeyAuthentication.HeaderName] = header;
        if (query is not null)
            context.Request.QueryString =
                new QueryString($"?{ApiKeyAuthentication.QueryName}={Uri.EscapeDataString(query)}");

        var loggerFactory = new LoggerFactory([_logs]);
        var handler = new ApiKeyAuthenticationHandler(
            new OptionsMonitorStub(), loggerFactory, UrlEncoder.Default, _repository);
        await handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthentication.Scheme, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private static async Task<bool> _IsAuthorizedAsync(ClaimsPrincipal? principal)
    {
        IAuthorizationService authorization = new ServiceCollection()
            .AddAuthorization()
            .AddLogging()
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

        AuthorizationResult result = await authorization.AuthorizeAsync(
            principal ?? new ClaimsPrincipal(), resource: null, ApiKeyAuthentication.BuildPolicy());
        return result.Succeeded;
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

        public void Dispose() { }

        private sealed class Capturing(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                lines.Add(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));
        }
    }
}
