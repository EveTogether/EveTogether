using System.Security.Claims;
using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Runtime;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-383: a delete from the control panel wrote straight to the store, so a composition, a shared fit or a fleet
/// removed there stayed on every connected client's screen until it reloaded. The panel now deletes through the module
/// commands, and their signal reaches the clients through the same relays a client's own delete does.
/// </summary>
public sealed class AdminDeleteBroadcastTests : IDisposable
{
    private const int Owner = 90250177;
    private const int Member = 90000002;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly RecordingWriter _owner = new();
    private readonly RecordingWriter _member = new();
    private readonly List<IHostedService> _relays = [];

    public AdminDeleteBroadcastTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddCqrs();
        services.AddEventBus();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddSingleton<IRuntimeContext>(new RuntimeContext(ExecutionHost.Server));
        services.AddFleetModule();
        services.AddSingleton<IDbContextFactory<SharedDbContext>>(_factory);
        services.AddSingleton<IDbContextFactory<ServerDbContext>>(_factory);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public void Dispose()
    {
        foreach (IDisposable relay in _relays.OfType<IDisposable>())
            relay.Dispose();
        _scope.Dispose();
        _provider.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task DeleteComposition_FromThePanel_ReachesAConnectedClient()
    {
        DataAdminService panel = await _PanelAsync();
        long compositionId = await _SeedAsync(new FleetComposition
        {
            Name = "Doctrine", OwnerCharacterId = Owner, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        }, composition => composition.Id);

        Result result = await panel.DeleteFleetCompositionAsync(_Admin(), compositionId, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Messages.FirstOrDefault()?.Text);
        CompositionChangePayload? change = JsonSerializer.Deserialize<CompositionChangePayload>(
            _member.Single("composition.changed").PayloadJson);
        Assert.Equal(compositionId, change?.CompositionId);
        Assert.Equal(CompositionChangeKind.Deleted, change?.Kind);
    }

    [Fact]
    public async Task DeleteSharedFit_FromThePanel_ReachesAConnectedClient()
    {
        DataAdminService panel = await _PanelAsync();
        int fitId = await _SeedAsync(new SharedFit
        {
            EsiFittingId = 1, Name = "Fit", ShipTypeId = 11987, SharedByCharacterId = Owner, SharedByCharacterName = "Owner",
            SharedAt = DateTimeOffset.UtcNow
        }, fit => fit.Id);

        Result result = await panel.DeleteSharedFitAsync(_Admin(), fitId, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Messages.FirstOrDefault()?.Text);
        Assert.Equal(fitId, JsonSerializer.Deserialize<FitDeletedPayload>(_member.Single("fittings.deleted").PayloadJson)?.ServerFitId);
    }

    [Fact]
    public async Task PurgeFleet_FromThePanel_ReachesItsFormerRoster()
    {
        DataAdminService panel = await _PanelAsync();
        long fleetId = await _SeedAsync(new Fleet
        {
            Name = "Invite only", Visibility = FleetVisibility.InviteOnly, CreatorCharacterId = Owner,
            CreatedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow
        }, fleet => fleet.Id);
        await _SeedAsync(new FleetMember
        {
            FleetId = fleetId, CharacterId = Member, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1, JoinTime = DateTimeOffset.UtcNow
        }, member => member.Id);

        Result result = await panel.PurgeFleetAsync(_Admin(), fleetId, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Messages.FirstOrDefault()?.Text);
        FleetChangePayload? change = JsonSerializer.Deserialize<FleetChangePayload>(_member.Single("fleet.changed").PayloadJson);
        Assert.Equal(fleetId, change?.FleetId);
        Assert.Equal(FleetChangeKind.Disbanded, change?.Kind);
        Assert.Equal("fleet.changed", _owner.Single("fleet.changed").EventType);
    }

    private async Task<DataAdminService> _PanelAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("owner", Owner, "Owner", _owner));
        clients.Add(new ConnectedClient("member", Member, "Member", _member));

        IServiceProvider services = _scope.ServiceProvider;
        var eventBus = services.GetRequiredService<IEventBus>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        _relays.AddRange([
            new CompositionChangeRelay(eventBus, clients, NullLogger<CompositionChangeRelay>.Instance),
            new SharedFitChangeRelay(eventBus, scopes, clients, NullLogger<SharedFitChangeRelay>.Instance),
            new FleetChangeAnnouncer(eventBus, scopes, clients, NullLogger<FleetChangeAnnouncer>.Instance)
        ]);
        foreach (IHostedService relay in _relays)
            await relay.StartAsync(cancellationToken);

        return new DataAdminService(_factory, services.GetRequiredService<ISharedFitReader>(), new ServerAuthRepository(_factory),
            services.GetRequiredService<IFleetCompositionReader>(), services.GetRequiredService<IDispatcher>(),
            UnusedReleaser.Create(_factory));
    }

    private async Task<TId> _SeedAsync<TEntity, TId>(TEntity entity, Func<TEntity, TId> id) where TEntity : class
    {
        await using var db = ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContext();
        db.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id(entity);
    }

    private static ClaimsPrincipal _Admin() =>
        new(new ClaimsIdentity([new Claim(AdminClaims.Permission, PanelPermissions.DataDelete)], "test"));

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

        public EventEnvelope Single(string eventType) =>
            Assert.Single(Written, written => written.Event.EventType == eventType).Event;
    }
}
