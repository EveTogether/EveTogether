namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>One character a homefront's attendance list has a row for (ET-230): on the fleet's roster, or one of this
/// client's own characters on the run. Per character and nothing more — the roster is decoupled from ownership, so
/// which characters belong to one player is not known and never guessed (Jithran, 2026-09-11).</summary>
/// <param name="IsLocal">One of this client's own characters — the one thing a client knows for certain.</param>
/// <param name="IsExternal">On the roster without an Eve Together client, so no evidence can ever arrive.</param>
public sealed record AttendanceCandidate(long CharacterId, string Name, bool IsLocal, bool IsExternal);
