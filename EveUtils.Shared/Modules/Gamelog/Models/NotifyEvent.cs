namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// A notify/warning gamelog line, surfaced verbatim (tags stripped) — e.g. warp-scramble interference,
/// ECM jam, energy neutralizer. Kept generic so the metrics view can list "notable events" without brittle
/// per-message parsing.
/// </summary>
public sealed record NotifyEvent(
    DateTime Timestamp,
    string Message) : GameLogEvent(Timestamp);
