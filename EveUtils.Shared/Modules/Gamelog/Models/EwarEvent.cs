using EveUtils.Shared.Modules.Sde.Storage;

namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>An enemy's warp scramble, warp disruption or ECM jam on the pilot; <paramref name="Source"/> is the enemy as the line names it.</summary>
public sealed record EwarEvent(DateTime Timestamp, NpcEwarKind Kind, string Source) : GameLogEvent(Timestamp);
