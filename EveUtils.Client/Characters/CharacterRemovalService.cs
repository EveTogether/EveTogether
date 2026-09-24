using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Characters;

/// <summary>
/// Removes one character from this PC (ET-345), in the order the ticket fixes: decouple it from every server — which
/// releases it and its EVE token there (ET-344), or queues that for the next connection when the server is out of
/// reach — then withdraw its sign-in here and revoke it at CCP, then delete what every module keeps for it.
/// </summary>
public sealed class CharacterRemovalService(
    IClientSessionStore sessions,
    ServerCouplingService coupling,
    ICharacterRegistry registry,
    IPerCharacterTokenStore tokenStore,
    ClientTokenRefreshService tokenRefresh,
    IEsiTokenRevoker revoker,
    EsiOptions esiOptions,
    GamelogClientService gamelog,
    ShipFitDetectionService fitDetection,
    IEnumerable<ICharacterDataEraser> erasers,
    IFleetRepository fleets,
    IFleetTransportClient fleetTransport,
    IServiceScopeFactory scopes,
    ILogger<CharacterRemovalService> logger) : ISingletonService
{
    // CCP's answer is not worth holding the removal hostage for: the revoke is best effort, and a grant it misses
    // expires on its own at CCP.
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(10);

    public async Task<CharacterRemovalCheck> CheckAsync(int characterId, CancellationToken cancellationToken = default)
    {
        var blockingFleet = await _FindActiveOwnFleetAsync(characterId, cancellationToken);

        using var scope = scopes.CreateScope();
        var running = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunsQuery(), cancellationToken);
        IReadOnlyList<Guid> openRuns = running.IsSuccess && running.Value is { } runs
            ? [.. runs.Where(run => run.CharacterId == characterId).Select(run => run.Id)]
            : [];

        return new CharacterRemovalCheck(blockingFleet, openRuns);
    }

    /// <summary>Removes the character. Never blocks on a server that cannot be reached; its address comes back in
    /// the result so the player is told the decouple is still pending there.</summary>
    /// <param name="dataToErase">Which kinds of the modules' data go with it — <see cref="CharacterDataKind.Cache"/>
    /// always, <see cref="CharacterDataKind.History"/> only when the player asked for it.</param>
    public async Task<IReadOnlyList<string>> RemoveAsync(int characterId, string characterName,
        IReadOnlySet<CharacterDataKind> dataToErase, CancellationToken cancellationToken = default)
    {
        List<string> unreachable = [];
        foreach (var server in await sessions.ListServersForCharacterAsync(characterId, cancellationToken))
            if (await coupling.DecoupleCharacterAsync(server, characterId, cancellationToken) == ServerRevokeOutcome.Unreachable)
                unreachable.Add(server);

        EsiTokenSet? tokens = await tokenRefresh.ForgetAsync(characterId, async () =>
        {
            var stored = await tokenStore.LoadAsync(characterId, cancellationToken);
            await tokenStore.RemoveAsync(characterId, cancellationToken);
            await registry.RemoveAsync(characterId, cancellationToken);
            return stored;
        }, cancellationToken);
        gamelog.ForgetCharacter(characterId);
        await fitDetection.ForgetCharacterAsync(characterId, cancellationToken);

        if (tokens is { RefreshToken: { Length: > 0 } refreshToken })
            await _RevokeAtCcpAsync(characterId, refreshToken, cancellationToken);

        foreach (var eraser in erasers.Where(eraser => dataToErase.Contains(eraser.Kind)))
            await eraser.EraseAsync(characterId, characterName, cancellationToken);

        return unreachable;
    }

    private async Task _RevokeAtCcpAsync(int characterId, string refreshToken, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RevokeTimeout);
        try
        {
            await revoker.RevokeRefreshTokenAsync(refreshToken, esiOptions.ClientId, esiOptions.ClientSecret, timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not revoke the EVE sign-in of removed character {CharacterId} at CCP; " +
                "it is deleted here and expires at CCP on its own.", characterId);
        }
    }

    // Only a fleet that is running right now: a stopped one has no commander to lose, and the ticket's way out is
    // exactly "stop it first". A server that cannot be asked is not a reason to refuse the removal.
    private async Task<string?> _FindActiveOwnFleetAsync(int characterId, CancellationToken cancellationToken)
    {
        foreach (var fleet in await fleets.ListByCreatorAsync(characterId, cancellationToken))
            if (fleet.IsClientOnly && _IsActive(fleet.State, fleet.Activation))
                return fleet.Name;

        foreach (var server in await sessions.ListServersForCharacterAsync(characterId, cancellationToken))
        {
            try
            {
                foreach (var fleet in await fleetTransport.ListMyFleetsAsync(server, characterId, cancellationToken: cancellationToken))
                    if (fleet.CreatorCharacterId == characterId && _IsActive(fleet.State, fleet.Activation))
                        return fleet.Name;
            }
            catch (FleetTransportException ex)
            {
                logger.LogDebug(ex, "Could not list the fleets of character {CharacterId} on {Server}.", characterId, server);
            }
        }

        return null;
    }

    private static bool _IsActive(FleetState state, FleetActivation activation) =>
        state == FleetState.Active && activation == FleetActivation.Active;
}
