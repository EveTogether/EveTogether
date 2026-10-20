using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <param name="CharacterId">The character that asks, 0 when there is none.</param>
/// <param name="Servers">The servers it is coupled to, opted in on and connected to.</param>
/// <param name="Reason">Why there is no server to ask, when there is none.</param>
/// <param name="Message">What to tell the pilot on a server tab that cannot be read.</param>
public sealed record OutcomeReader(int CharacterId, string[] Servers, OwnRunsReason Reason, string? Message);

/// <summary>Who may ask which server for the shared outcomes (ET-517, ET-519): reading is part of opting in.</summary>
public static class OutcomeReaders
{
    /// <summary>The pilot's first character that is coupled to, opted in on and connected to a server (the one asked, on a
    /// server tab), with the servers that qualifies it for. Otherwise the reason there is none.</summary>
    public static async Task<OutcomeReader> FindAsync(IServiceScopeFactory scopeFactory, RunOutcomeSharingChoices choices,
        IRemoteBusConnector connector, IReadOnlyList<int> characterIds, string? onlyServer, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IClientSessionStore store = scope.ServiceProvider.GetRequiredService<IClientSessionStore>();
        IReadOnlyDictionary<string, OutcomeSharingChoice> chosen = await choices.LoadAsync(cancellationToken);
        OwnRunsReason reason = OwnRunsReason.None;
        string? message = null;
        foreach (int characterId in characterIds.Order())
        {
            string[] coupled = [.. (await store.ListServersForCharacterAsync(characterId, cancellationToken))
                .Where(server => onlyServer is null || server == onlyServer)];
            if (coupled.Length == 0)
                continue;

            string[] sharing = [.. coupled.Where(server => chosen.GetValueOrDefault(server) is OutcomeSharingChoice.Shared).Order(StringComparer.Ordinal)];
            if (sharing.Length == 0)
            {
                reason = OwnRunsReason.NotSharing;
                message ??= "Turn on sharing in Settings to see what this server's pilots earn.";
                continue;
            }

            string[] connected = [.. sharing.Where(server => connector.StateFor(server, characterId) == ServerConnectionState.Connected)];
            if (connected.Length > 0)
                return new OutcomeReader(characterId, connected, OwnRunsReason.None, null);

            if (reason is not OwnRunsReason.NotSharing)
                reason = OwnRunsReason.ServerUnavailable;
            message = "Not connected to this server.";
        }

        return new OutcomeReader(0, [], reason, onlyServer is null ? null : message ?? "None of your characters is coupled to this server.");
    }
}
