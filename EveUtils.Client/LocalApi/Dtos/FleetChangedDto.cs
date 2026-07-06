using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A fleet lifecycle/roster change pushed over the <c>/ws</c> stream (the <c>fleet.changed</c> event), so a widget
/// knows to re-read <c>/fleet</c>. <c>Kind</c> is the change kind (activation/conclusion/join/leave/move/…);
/// <c>ServerAddress</c> is the originating server (fleet ids are per-server), null for a client-only fleet.
/// </summary>
public sealed record FleetChangedDto(long FleetId, string Kind, string? ServerAddress)
{
    public static FleetChangedDto FromEvent(FleetChangedEvent changed) =>
        new(changed.Data.FleetId, changed.Data.Kind.ToString(), changed.SourceServerAddress);
}
