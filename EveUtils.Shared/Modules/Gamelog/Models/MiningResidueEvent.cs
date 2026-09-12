namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>The residue line's own form — "Additional N units depleted from asteroid as residue" — which carries no
/// ore name (ET-229). It always immediately follows, in the same second, the "You mined" line it belongs to
/// (measured against the 2026-08-28/29 Metaliminal logs, zero exceptions), so a caller correlates it to whichever ore
/// that character was last seen mining rather than this event naming one itself.</summary>
public sealed record MiningResidueEvent(DateTime Timestamp, int Units) : GameLogEvent(Timestamp);
