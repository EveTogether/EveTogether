namespace EveUtils.Client.Esi;

/// <summary>The result of inviting one planned pilot to the live in-game fleet: the character, whether the
/// invitation was sent, and the ESI reason when it failed (a CSPA charge, or the target wing/squad not pushed yet).</summary>
public sealed record EsiInviteOutcome(int CharacterId, EsiInviteStatus Status, string? Message);
