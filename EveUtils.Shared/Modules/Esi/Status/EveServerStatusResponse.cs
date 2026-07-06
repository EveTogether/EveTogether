namespace EveUtils.Shared.Modules.Esi.Status;

/// <summary>
/// The ESI <c>GET /status/</c> wire shape (only the fields the client surfaces). <c>players</c> is always
/// present on a 200; <c>vip</c> is only sent while Tranquility is in VIP mode, so it defaults to false.
/// Web JSON defaults match these names case-insensitively (no <c>[JsonPropertyName]</c> needed).
/// </summary>
public sealed class EveServerStatusResponse
{
    public int Players { get; init; }

    public bool Vip { get; init; }
}
