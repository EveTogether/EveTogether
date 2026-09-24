using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Runtime;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using EveUtils.Shared.Cqrs.Permissions;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-11: a composition changed through the gRPC service is pushed to the other connected characters as
/// <c>composition.changed</c>, carrying the composition that really changed — also when the RPC only names a role or
/// an entry, and also when it deletes the very row that tells which composition it belonged to. The acting character is
/// left out (its own client already published the change), and a composition that never reaches the server (client-only)
/// is announced to nobody.
/// </summary>
public sealed class CompositionChangeRelayTests : IDisposable
{
    private const int Jithran = 90250177;
    private const int Raymond = 90000002;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly RecordingWriter _actor = new();
    private readonly RecordingWriter _viewer = new();

    public CompositionChangeRelayTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddCqrs();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddFleetModule();
        services.AddSingleton<IDbContextFactory<SharedDbContext>>(_factory);
        services.AddSingleton<IDbContextFactory<ServerDbContext>>(_factory);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ARoleOrEntryChange_ReachesOtherCharactersWithTheOwningComposition_AndNotTheActor()
    {
        var (service, token) = await _ServiceAsync();
        var context = _Context(token);

        var created = await service.CreateFleetComposition(new CreateFleetCompositionRequest { Name = "Armor doctrine" }, context);
        var role = await service.AddFleetCompositionRole(
            new AddFleetCompositionRoleRequest { CompositionId = created.Id, RoleName = "Logistics" }, context);
        var entry = await service.AddFleetCompositionEntry(new AddFleetCompositionEntryRequest
        {
            RoleId = role.Id,
            Fit = new FitReferenceDto { ShipTypeId = 11987, FitName = "Guardian", RawJson = "{}", ContentHash = "h-guardian" }
        }, context);
        Assert.True(entry.Accepted, entry.Message);
        var edited = await service.EditFleetCompositionEntry(new EditFleetCompositionEntryRequest { EntryId = entry.Id, EntryMinCount = 3 }, context);
        var entryRemoved = await service.RemoveFleetCompositionEntry(new RemoveFleetCompositionEntryRequest { EntryId = entry.Id }, context);
        var roleRemoved = await service.RemoveFleetCompositionRole(new RemoveFleetCompositionRoleRequest { RoleId = role.Id }, context);

        Assert.Equal(
            [
                (created.Id, CompositionChangeKind.Created),
                (created.Id, CompositionChangeKind.Edited), // role added
                (created.Id, CompositionChangeKind.Edited), // entry added
                (created.Id, CompositionChangeKind.Edited), // entry edited
                (created.Id, CompositionChangeKind.Edited), // entry removed — resolved before the row was deleted
                (created.Id, CompositionChangeKind.Edited)  // role removed — same
            ],
            _viewer.Changes());
        Assert.Empty(_actor.Written);
        Assert.All([created.CompositionId, role.CompositionId, entry.CompositionId, edited.CompositionId,
            entryRemoved.CompositionId, roleRemoved.CompositionId], id => Assert.Equal(created.Id, id));
    }

    [Fact]
    public async Task AClientOnlyComposition_IsAnnouncedToNobody()
    {
        var (service, token) = await _ServiceAsync();

        var created = await service.CreateFleetComposition(
            new CreateFleetCompositionRequest { Name = "Local only", IsClientOnly = true }, _Context(token));

        Assert.True(created.Accepted, created.Message);
        Assert.Empty(_viewer.Written);
        Assert.Empty(_actor.Written);
    }

    private async Task<(FleetsGrpcService Service, string AccessToken)> _ServiceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authRepository = new ServerAuthRepository(_factory);
        var character = await authRepository.UpsertSyncedAsync(Jithran, "Jithran", new EncryptedToken([1], [2], [3]), null, cancellationToken);
        var sessions = new ServerSessionService(authRepository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, cancellationToken);

        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("jithran", Jithran, "Jithran", _actor));
        clients.Add(new ConnectedClient("raymond", Raymond, "Raymond", _viewer));

        var services = _scope.ServiceProvider;
        var fleets = services.GetRequiredService<IFleetRepository>();
        var service = new FleetsGrpcService(
            sessions, services.GetRequiredService<IDispatcher>(), clients, new FleetChangeAnnouncer(fleets, clients), fleets,
            services.GetRequiredService<IFleetCompositionRepository>(), services.GetRequiredService<FleetCompositionAuthorizer>(),
            authRepository);
        return (service, issued.AccessToken);
    }

    private static ServerCallContext _Context(string bearer) =>
        new HeadersOnlyCallContext(new Metadata { { "authorization", $"Bearer {bearer}" } });

    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        public List<ServerEnvelope> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }

        public List<(long CompositionId, CompositionChangeKind Kind)> Changes() => Written
            .Where(w => w.Event.EventType == "composition.changed")
            .Select(w => JsonSerializer.Deserialize<CompositionChangePayload>(w.Event.PayloadJson)!)
            .Select(p => (p.CompositionId, p.Kind))
            .ToList();
    }

    private sealed class HeadersOnlyCallContext(Metadata headers) : ServerCallContext
    {
        protected override Metadata RequestHeadersCore => headers;
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
