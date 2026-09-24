using System.Net;
using System.Security.Claims;
using System.Text;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-344: decoupling the last session of a character ends the server's hold on its EVE grant — the row and its
/// token are deleted and the token is revoked at CCP through the real auth client, with a fake HTTP handler in place
/// of CCP. A character another machine is still coupled to keeps both.
/// </summary>
public sealed class SyncedCharacterReleaseTests : IDisposable
{
    private const string RefreshToken = "eve-refresh-token";

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServerAuthRepository _repository;
    private readonly ServerSessionService _sessions;
    private readonly FakeCcpHandler _ccp = new();
    private readonly SessionService _grpc;
    private readonly SyncedCharacterReleaser _releaser;

    public SyncedCharacterReleaseTests()
    {
        _repository = new ServerAuthRepository(_factory);
        _sessions = new ServerSessionService(_repository, NullLogger<ServerSessionService>.Instance);
        _releaser = new SyncedCharacterReleaser(
            _repository, new PlainTokenProtector(), new EsiAuthClient(new SingleClientFactory(new HttpClient(_ccp))),
            new EsiOptions { ClientId = "app-id", ClientSecret = "app-secret" }, NullLogger<SyncedCharacterReleaser>.Instance);
        _grpc = new SessionService(_sessions, _releaser);
    }

    [Fact]
    public async Task Revoke_LastSessionOfACharacter_DeletesTheCharacterAndRevokesItsTokenAtCcp()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await _PairAsync(90382598, "Abnoba Auscent", ct);

        var reply = await _grpc.Revoke(new RevokeRequest { SessionToken = session.AccessToken }, new NoContext());

        Assert.True(reply.Ok);
        Assert.Empty(await _repository.ListSyncedAsync(ct));
        var request = Assert.Single(_ccp.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://login.eveonline.com/v2/oauth/revoke", request.Uri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("app-id:app-secret")), request.Authorization);
        Assert.Equal($"token={RefreshToken}&token_type_hint=refresh_token", request.Body);
    }

    [Fact]
    public async Task Revoke_WhileAnotherMachineIsStillCoupled_KeepsTheCharacterAndItsToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var machineA = await _PairAsync(90382598, "Abnoba Auscent", ct);
        var machineB = await _sessions.IssueAsync(machineA.SyncedCharacterId, ct);

        var reply = await _grpc.Revoke(new RevokeRequest { SessionToken = machineA.AccessToken }, new NoContext());

        Assert.True(reply.Ok);
        var kept = Assert.Single(await _repository.ListSyncedAsync(ct));
        Assert.Equal(RefreshToken, new PlainTokenProtector().Unprotect(new EncryptedToken(kept.RefreshTokenCipher, kept.RefreshTokenNonce, kept.RefreshTokenTag)));
        Assert.Empty(_ccp.Requests);
        Assert.NotNull(await _sessions.ValidateAsync(machineB.AccessToken, ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Revoke_WhenCcpRefusesTheRevoke_StillDeletesTheCharacter(HttpStatusCode ccpStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        _ccp.Status = ccpStatus;
        var session = await _PairAsync(90382598, "Abnoba Auscent", ct);

        var reply = await _grpc.Revoke(new RevokeRequest { SessionToken = session.AccessToken }, new NoContext());

        Assert.True(reply.Ok);
        Assert.Empty(await _repository.ListSyncedAsync(ct));
        Assert.Single(_ccp.Requests);
    }

    [Fact]
    public async Task Revoke_WhenCcpIsUnreachable_StillDeletesTheCharacter()
    {
        var ct = TestContext.Current.CancellationToken;
        _ccp.Failure = new HttpRequestException("no route to host");
        var session = await _PairAsync(90382598, "Abnoba Auscent", ct);

        var reply = await _grpc.Revoke(new RevokeRequest { SessionToken = session.AccessToken }, new NoContext());

        Assert.True(reply.Ok);
        Assert.Empty(await _repository.ListSyncedAsync(ct));
    }

    [Fact]
    public async Task Revoke_UnknownSession_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _PairAsync(90382598, "Abnoba Auscent", ct);

        var reply = await _grpc.Revoke(new RevokeRequest { SessionToken = "not-a-session" }, new NoContext());

        Assert.False(reply.Ok);
        Assert.Single(await _repository.ListSyncedAsync(ct));
        Assert.Empty(_ccp.Requests);
    }

    [Fact]
    public async Task Sweep_ReleasesEveryCharacterWithoutASession_AndKeepsTheCoupledOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertSyncedAsync(1, "Left behind one", new PlainTokenProtector().Protect("orphan-one"), null, ct);
        var coupled = await _PairAsync(2, "Still coupled", ct);
        await _repository.UpsertSyncedAsync(3, "Left behind two", new PlainTokenProtector().Protect("orphan-two"), null, ct);
        var services = new ServiceCollection()
            .AddSingleton<IServerAuthRepository>(_repository)
            .AddSingleton(_releaser)
            .BuildServiceProvider();
        var cleanup = new ServerSessionCleanupService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ServerSessionCleanupService>.Instance);

        await cleanup.SweepAsync(ct);

        var remaining = Assert.Single(await _repository.ListSyncedAsync(ct));
        Assert.Equal(coupled.SyncedCharacterId, remaining.Id);
        Assert.Equal(["token=orphan-one&token_type_hint=refresh_token", "token=orphan-two&token_type_hint=refresh_token"],
            _ccp.Requests.Select(r => r.Body).Order().ToArray());
    }

    [Fact]
    public async Task DeleteSyncedCharacter_ByAnAdmin_DeletesTheCharacterAndRevokesItsTokenAtCcp()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await _PairAsync(90382598, "Abnoba Auscent", ct);
        var dataAdmin = new DataAdminService(
            _factory, new SharedFitRepository(_factory), _repository, new FleetCompositionRepository(_factory), new UnusedDispatcher(), _releaser);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AdminClaims.Permission, PanelPermissions.DataDelete)], "test"));

        var result = await dataAdmin.DeleteSyncedCharacterAsync(admin, session.SyncedCharacterId, ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(await _repository.ListSyncedAsync(ct));
        var request = Assert.Single(_ccp.Requests);
        Assert.Equal("https://login.eveonline.com/v2/oauth/revoke", request.Uri);
        Assert.Equal($"token={RefreshToken}&token_type_hint=refresh_token", request.Body);
    }

    private async Task<(string AccessToken, int SyncedCharacterId)> _PairAsync(int esiCharacterId, string name, CancellationToken ct)
    {
        var synced = await _repository.UpsertSyncedAsync(esiCharacterId, name, new PlainTokenProtector().Protect(RefreshToken), null, ct);
        var issued = await _sessions.IssueAsync(synced.Id, ct);
        return (issued.AccessToken, synced.Id);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record CcpRequest(HttpMethod Method, string Uri, string? Authorization, string Body);

    private sealed class FakeCcpHandler : HttpMessageHandler
    {
        public List<CcpRequest> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Exception? Failure { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CcpRequest(
                request.Method, request.RequestUri?.ToString() ?? string.Empty, request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return Failure is null ? new HttpResponseMessage(Status) : throw Failure;
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class PlainTokenProtector : ITokenProtector
    {
        public EncryptedToken Protect(string plaintext) => new(Encoding.UTF8.GetBytes(plaintext), [1], [2]);

        public string Unprotect(EncryptedToken token) => Encoding.UTF8.GetString(token.Cipher);
    }

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
