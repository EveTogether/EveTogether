namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>Base for a parsed EVE gamelog line. Folded from the EVE-Utils demo (own code).</summary>
public abstract record GameLogEvent(DateTime Timestamp);
