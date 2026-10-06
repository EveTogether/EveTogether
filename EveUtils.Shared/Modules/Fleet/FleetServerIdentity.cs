using System.Security.Cryptography;
using System.Text;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// The one key a fleet's server goes by on this client: a fleet id is only unique per server, so anything stored per
/// fleet carries this too. A client-only fleet has no server and reads as "local". Case and a trailing slash do not matter.
/// </summary>
public static class FleetServerIdentity
{
    public static string Of(string? serverAddress)
    {
        string identity = string.IsNullOrWhiteSpace(serverAddress)
            ? "local"
            : serverAddress.Trim().TrimEnd('/').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}
