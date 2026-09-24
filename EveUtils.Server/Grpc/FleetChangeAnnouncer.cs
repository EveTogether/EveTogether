using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Server.Grpc;

/// <summary>
/// The server's relay for the fleet signal (ET-381). Every fleet command handler publishes <see cref="FleetChangedEvent"/>
/// on the local bus once its write is done; this decides who hears it over the wire, so neither a handler nor a gRPC
/// method has to remember to announce anything.
///
/// <para>A lifecycle change — created, edited, started, stopped (by hand or by the auto-stop), concluded, disbanded — of
/// a fleet the discovery list shows reaches every connected character (ET-10): the fleet list is where a non-member
/// watches a public fleet. Listed before the change counts as much as listed after it (ET-360): a conclude, a disband or
/// an edit to invite-only takes the row out of discovery, and the non-members still have to see it go. Every other
/// change, and any change to a fleet discovery hides, stays with the fleet's own audience: its roster, its owner, the
/// character who acted — the echo rule, so a requester or a member who just left hears their own change back — a
/// member the change took off the roster, and the whole former roster of a fleet the change deleted (ET-383).</para>
///
/// <para>The payload is the fleet id, the kind and a stop trigger — nothing the discovery list does not already show,
/// no roster, no member ids — so one envelope serves both audiences and each connection receives it once.</para>
///
/// <para>A bus subscriber runs inside the publishing command, after its commit, so this never throws: a push that
/// fails is logged, and the change stays saved.</para>
/// </summary>
public sealed class FleetChangeAnnouncer(
    IEventBus eventBus,
    IServiceScopeFactory scopes,
    ConnectedClients connectedClients,
    ILogger<FleetChangeAnnouncer> logger) : IHostedService, IDisposable
{
    private static readonly FleetChangeKind[] LifecycleKinds =
    [
        FleetChangeKind.Created, FleetChangeKind.Edited, FleetChangeKind.Activated,
        FleetChangeKind.Stopped, FleetChangeKind.Concluded, FleetChangeKind.Disbanded
    ];

    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = eventBus.Subscribe<FleetChangedEvent>(_RelayAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private async Task _RelayAsync(FleetChangedEvent change, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IFleetReader>();
            var recipients = await _AudienceAsync(repository, change, cancellationToken);
            await connectedClients.SendToCharactersAsync(recipients, WireEnvelopeFactory.ToEnvelope(change), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Pushing the {Kind} change of fleet {FleetId} failed; its watchers see it on their next read.",
                change.Data.Kind, change.FleetId);
        }
    }

    private async Task<IEnumerable<int>> _AudienceAsync(
        IFleetReader repository, FleetChangedEvent change, CancellationToken cancellationToken)
    {
        if (LifecycleKinds.Contains(change.Data.Kind)
            && (change.WasListed || await repository.IsOpenAsync(change.FleetId, cancellationToken)))
            return connectedClients.ConnectedCharacters().Select(c => c.CharacterId);

        var members = await repository.ListMembersAsync(change.FleetId, cancellationToken);
        var fleet = await repository.GetAsync(change.FleetId, cancellationToken);
        int?[] alsoConcerned = [fleet?.CreatorCharacterId, change.ActingCharacterId, change.FormerMemberCharacterId];
        return members.Select(m => m.CharacterId)
            .Concat(alsoConcerned.OfType<int>())
            .Concat(change.FormerRosterCharacterIds)
            .Distinct();
    }
}
