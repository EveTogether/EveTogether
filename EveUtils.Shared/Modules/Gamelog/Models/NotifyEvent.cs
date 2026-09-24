namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// A notify/warning gamelog line, surfaced verbatim (tags stripped) — e.g. warp-scramble interference,
/// ECM jam, energy neutralizer. Kept generic so the metrics view can list "notable events" without brittle
/// per-message parsing. <paramref name="Language"/> is the client language the message is written in, which the
/// few consumers that do read one (<see cref="Parsing.GamelogNotices"/>) need to know.
/// </summary>
public sealed record NotifyEvent(
    DateTime Timestamp,
    string Message,
    GamelogLanguage Language = GamelogLanguage.English) : GameLogEvent(Timestamp);
