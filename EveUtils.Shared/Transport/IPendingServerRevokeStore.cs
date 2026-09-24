namespace EveUtils.Shared.Transport;

/// <summary>The decouples still waiting to reach a server that could not be reached when they were made.</summary>
public interface IPendingServerRevokeStore
{
    Task QueueAsync(string serverAddress, int characterId, string accessToken, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingServerRevoke>> ListAsync(string serverAddress, CancellationToken cancellationToken = default);

    Task RemoveAsync(int id, CancellationToken cancellationToken = default);
}
