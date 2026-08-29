namespace EveUtils.Shared.Modules.Esi.Events;

/// <summary>
/// The outcome of a token check for one character: which character, and what its ESI session is worth
/// right now. Payload of <see cref="TokenRefreshedEvent"/> / <see cref="TokenRefreshFailedEvent"/>.
/// </summary>
public sealed record TokenStatusChange(int CharacterId, TokenStatus Status);
