using EveUtils.Server.Auth;
using EveUtils.Shared.Modules.ServerAuth.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>One session row. The status is fixed when the list loads, so sorting and grouping agree within one view.</summary>
public sealed class SessionListItem
{
    public required ServerSession Session { get; init; }
    public required SessionPanelStatus Status { get; init; }

    /// <summary>The Dashboard's rule: live within the heartbeat window, lapsing once the cleanup sweep would remove it.</summary>
    public static SessionPanelStatus StatusOf(DateTimeOffset lastHeartbeat, DateTimeOffset now) =>
        now - lastHeartbeat <= ServerSessionService.LiveWindow ? SessionPanelStatus.Live
        : now - lastHeartbeat >= ServerSessionService.IdleLifetime ? SessionPanelStatus.Lapsing
        : SessionPanelStatus.Idle;
}
