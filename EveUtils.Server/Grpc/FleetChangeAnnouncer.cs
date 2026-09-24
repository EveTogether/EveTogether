using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Pushes a fleet's lifecycle change — created, edited, started, stopped (by hand or by the auto-stop), concluded, disbanded —
/// to whoever has that fleet on screen (ET-10). For a fleet the discovery list shows, that is every connected
/// character: the fleet list is where a non-member watches a public fleet, and before this they saw its old status
/// until they pressed Refresh. Any other fleet stays on its roster audience, because broadcasting it would announce a
/// fleet discovery deliberately hides.
///
/// <para>The payload is the fleet id, the kind and a stop trigger — nothing the discovery list does not already show,
/// no roster, no member ids — so one envelope serves both audiences and each connection receives it once.</para>
/// </summary>
public sealed class FleetChangeAnnouncer(IFleetRepository repository, ConnectedClients connectedClients)
{
    /// <param name="rosterAudience">Who hears it when the fleet is not listed: its members, plus anyone else the
    /// caller must reach (the auto-stop addresses the owner as well).</param>
    /// <param name="listedBeforeChange">Whether the fleet was listed before the change. A conclude or a disband takes
    /// the fleet out of discovery, and its non-members still need to see the row go.</param>
    public async Task AnnounceAsync(
        FleetChangePayload change,
        IEnumerable<int> rosterAudience,
        bool listedBeforeChange,
        CancellationToken cancellationToken)
    {
        var envelope = WireEnvelopeFactory.ToEnvelope(new FleetChangedEvent(change));
        var listed = listedBeforeChange || await repository.IsOpenAsync(change.FleetId, cancellationToken);
        var recipients = listed
            ? connectedClients.ConnectedCharacters().Select(c => c.CharacterId)
            : rosterAudience;
        await connectedClients.SendToCharactersAsync(recipients, envelope, cancellationToken);
    }
}
