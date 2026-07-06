using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Shared.Modules.Esi.Status;

/// <summary>
/// An immutable view of the Tranquility status for the UI: the coarse <see cref="State"/> plus the live
/// player count when known. Value equality lets the poller skip raising a change when nothing moved.
/// </summary>
public sealed record EveServerStatusSnapshot(EveServerState State, int? Players)
{
    /// <summary>The pre-first-poll value the UI starts from.</summary>
    public static EveServerStatusSnapshot Unknown { get; } = new(EveServerState.Unknown, null);

    /// <summary>Maps a <c>/status/</c> call outcome onto a snapshot.</summary>
    public static EveServerStatusSnapshot From(EsiResult<EveServerStatusResponse> result)
    {
        if (result is { IsSuccess: true, Value: { } status })
            return new(status.Vip ? EveServerState.Vip : EveServerState.Online, status.Players);

        // A 5xx (503 during downtime) means Tranquility is down. A network/timeout failure only means we
        // could not reach ESI — stay Unknown rather than blame the game server for our own connectivity blip.
        var state = result.Error?.Kind == EsiErrorKind.ServerError ? EveServerState.Offline : EveServerState.Unknown;
        return new(state, null);
    }
}
