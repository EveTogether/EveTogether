using System.Collections.Generic;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// An immutable view of the local widget API host state for the UI. <see cref="BoundAddresses"/> are the
/// addresses Kestrel actually bound (always loopback by construction) — surfaced so a test can prove the
/// host never listens on an external interface.
/// </summary>
public sealed record LocalApiStatusSnapshot(
    LocalApiStatus Status,
    int Port,
    string? Url = null,
    string? Message = null,
    IReadOnlyList<string>? BoundAddresses = null)
{
    public static LocalApiStatusSnapshot Stopped(int port) => new(LocalApiStatus.Stopped, port);
}
