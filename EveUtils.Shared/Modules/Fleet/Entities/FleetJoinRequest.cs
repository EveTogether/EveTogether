namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// A durable request to join an invite-only fleet: the mirror of <see cref="FleetInvite"/> for the
/// other direction. A character asks to join; the fleet owner accepts or denies. The request is the source of
/// truth that survives an offline owner, and its id rides a queued message to the owner via <c>RefId</c>.
/// </summary>
public sealed class FleetJoinRequest
{
    public long Id { get; set; }
    public long FleetId { get; set; }

    public int RequesterCharacterId { get; set; }

    public FleetJoinRequestStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}
