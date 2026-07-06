using EveUtils.Server.Permissions;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;

namespace EveUtils.Server.Auth;

/// <summary>
/// Completes a pairing once the SSO code is in (shared by the gRPC <c>RelayCode</c> loopback-relay
/// path and the server's own SSO callback endpoint): verifies CSRF state, does the confidential token
/// exchange (server's own app secret), validates the JWT, enforces the allowed-list, stores the
/// encrypted refresh token and issues a server session — marking the pairing completed.
/// </summary>
public sealed class PairingCompleter(
    EsiOptions esiOptions,
    IEsiAuthClient esiAuthClient,
    IEsiJwtValidator jwtValidator,
    IEsiAffiliationResolver affiliationResolver,
    IServerAuthRepository repository,
    ITokenProtector tokenProtector,
    IPermissionToggleStore toggles,
    ServerSessionService sessionService) : IScopedService
{
    public async Task<(bool Ok, string Message)> CompleteAsync(
        PairingState state, string code, string callbackState, CancellationToken cancellationToken)
    {
        if (!string.Equals(state.OAuthState, callbackState, StringComparison.Ordinal))
        {
            Fail(state, "OAuth state mismatch.");
            return (false, "OAuth state mismatch (possible CSRF).");
        }

        if (string.IsNullOrEmpty(esiOptions.ClientSecret))
        {
            Fail(state, "Server ESI client secret is not configured.");
            return (false, "Server ESI client secret is not configured (appsettings.Development.json).");
        }

        try
        {
            var tokens = await esiAuthClient.ExchangeConfidentialAsync(
                code, esiOptions.ClientId, esiOptions.ClientSecret, cancellationToken);
            var identity = await jwtValidator.ValidateAsync(tokens.AccessToken, esiOptions.ClientId, cancellationToken);

            // Public-server mode: when the allowed-list is disabled the gate is skipped — anyone who
            // completes the ESI auth-flow can pair. The auth-flow (token exchange + JWT validation) stays required.
            if (toggles.IsEnabled(ServerToggles.AllowedListEnabled))
            {
                var allowed = await repository.FindAllowedAsync(identity.CharacterId, identity.CharacterName, cancellationToken);
                if (allowed is null)
                {
                    Fail(state, $"{identity.CharacterName} is not on the allowed-list.");
                    return (false, $"{identity.CharacterName} is not on the allowed-list.");
                }
            }

            var affiliation = await affiliationResolver.ResolveAsync(identity.CharacterId, cancellationToken);

            var encrypted = tokenProtector.Protect(tokens.RefreshToken ?? string.Empty);
            var synced = await repository.UpsertSyncedAsync(identity.CharacterId, identity.CharacterName, encrypted, identity.GrantedScopes, cancellationToken);
            var session = await sessionService.IssueAsync(synced.Id, cancellationToken);

            state.CharacterId = identity.CharacterId;
            state.CharacterName = identity.CharacterName;
            state.CorporationName = affiliation?.CorporationName ?? string.Empty;
            state.AllianceName = affiliation?.AllianceName;
            state.SessionToken = session.AccessToken;
            state.SessionRefreshToken = session.RefreshToken;
            state.Status = PairingStatus.Completed;

            return (true, $"Paired {identity.CharacterName}.");
        }
        catch (Exception ex)
        {
            Fail(state, ex.Message);
            return (false, ex.Message);
        }
    }

    private static void Fail(PairingState state, string message)
    {
        state.Status = PairingStatus.Failed;
        state.FailureMessage = message;
    }
}
