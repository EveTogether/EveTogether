using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Transport;

public interface IServerSessionRevoker
{
    Task<ServerRevokeOutcome> RevokeAsync(string serverAddress, string sessionToken, CancellationToken cancellationToken = default);
}
