using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Shared.Transport;

/// <summary>
/// Local registry of coupled servers: remembers a user label and the server's own name per
/// address so the UI can show a friendly name instead of the raw URL. Stored alongside the trust/session
/// files in the client data dir.
/// </summary>
public interface IServerRegistry
{
    /// <summary>
    /// Records (or merges) what we know about a server. Null arguments leave the existing value intact,
    /// so a couple can set the label while pairing fills in the server's own name.
    /// </summary>
    Task SetAsync(string serverAddress, string? label, string? serverName, CancellationToken cancellationToken = default);

    Task<ServerInfo?> GetAsync(string serverAddress, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ServerInfo>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolved display name for a server (label → server name → raw address).</summary>
    Task<string> DisplayNameAsync(string serverAddress, CancellationToken cancellationToken = default);
}
