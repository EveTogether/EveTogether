namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// A durable invite: the source of truth that survives an offline invitee. <see cref="WingId"/>/
/// <see cref="SquadId"/> are conditional per role (ESI invite-body shape), hence nullable.
/// </summary>
public sealed class FleetInvite
{
    public long Id { get; set; }
    public long FleetId { get; set; }

    public int InviterCharacterId { get; set; }
    public int InviteeCharacterId { get; set; }

    public FleetRole Role { get; set; }
    public long? WingId { get; set; }
    public long? SquadId { get; set; }

    /// <summary>Optional free-text note the inviter sends along; surfaced as the inbox message body.</summary>
    public string? Message { get; set; }

    public FleetInviteStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}
