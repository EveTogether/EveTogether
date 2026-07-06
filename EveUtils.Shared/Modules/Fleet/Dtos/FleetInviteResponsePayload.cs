namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// Wire payload of an invite response, pushed back to the inviter and returned by the respond handler.
/// </summary>
public sealed record FleetInviteResponsePayload(
    long InviteId,
    long FleetId,
    int InviterCharacterId,
    int InviteeCharacterId,
    bool Accepted);
