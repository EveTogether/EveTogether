namespace EveUtils.Shared.Modules.Gamelog.Dtos;

/// <summary>Live DPS snapshot — the payload streamed over the event bus (<see cref="Events.CombatLoggedEvent"/>).</summary>
public sealed record DpsSampleDto(int? CharacterId, string CharacterName, long DealtPerSecond, long ReceivedPerSecond, DateTimeOffset At);
