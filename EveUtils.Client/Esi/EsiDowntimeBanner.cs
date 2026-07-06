using EveUtils.Shared.Modules.Esi.Status;

namespace EveUtils.Client.Esi;

/// <summary>
/// Resolves the top-of-window downtime banner from ESI availability + the Tranquility status, so the banner
/// is consistent with the gate: it shows whenever non-essential calls are being withheld — a failed
/// poll (maintenance or unreachable), not only a confirmed 5xx — plus VIP's limited access. Pure for testing.
/// </summary>
public static class EsiDowntimeBanner
{
    public static (bool Show, string Message) For(bool esiUsable, EveServerState status) => (esiUsable, status) switch
    {
        (false, EveServerState.Offline) =>
            (true, "EVE Tranquility is in maintenance — non-essential ESI calls are paused and resume automatically."),
        (false, _) =>
            (true, "Can't reach ESI right now — non-essential calls are paused until the connection is back."),
        (_, EveServerState.Vip) =>
            (true, "EVE is in VIP mode after downtime — ESI access is limited until it lifts."),
        _ => (false, "")
    };
}
