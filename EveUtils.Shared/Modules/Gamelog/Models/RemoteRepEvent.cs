namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// A remote repair/boost combat line. EVE only logs <em>remote</em> reps (logi → you, or you →
/// fleetmate), never your own local self-rep. <see cref="Outgoing"/> = you repped someone ("… to …"),
/// else you were repped ("… by …").
/// </summary>
public sealed record RemoteRepEvent(
    DateTime Timestamp,
    bool Outgoing,
    int Amount,
    string Kind,
    string Counterparty) : GameLogEvent(Timestamp);
