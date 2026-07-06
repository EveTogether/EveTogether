namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// The public <c>GET /status/</c> endpoint — the one ESI call that must stay exempt from the downtime guards so the
/// client keeps detecting recovery. The gate lets it through and the retry handler does not retry it: a failed status
/// poll is itself the "ESI is down" signal, so retrying it just bursts a dead API. Centralised here so the gate, the
/// retry handler and the pivot recognise it the same way.
/// </summary>
public static class EsiStatusEndpoint
{
    /// <summary>The ESI path the status poller calls (relative to the data base URL).</summary>
    public const string Path = "/status/";

    public static bool IsStatusPath(string? path) =>
        path is not null && path.TrimEnd('/').EndsWith("/status", StringComparison.OrdinalIgnoreCase);

    public static bool IsStatusPoll(Uri? uri) => uri is not null && IsStatusPath(uri.AbsolutePath);
}
