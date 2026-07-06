using System.Security.Cryptography;
using System.Text;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Shared.Modules.Esi;
using Grpc.Core;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Mode B pairing. StartPairing returns an authorize URL whose redirect goes to
/// the server's own SSO callback (the server has its own ESI app + callback); that endpoint completes
/// the pairing. <c>RelayCode</c> remains as the Tier-0 loopback-relay fallback. Both finish via
/// <see cref="PairingCompleter"/>. CSRF is covered by oauth_state, the claim by the pairing secret.
/// </summary>
public sealed partial class PairingService
{
    public override Task<StartPairingReply> StartPairing(StartPairingRequest request, ServerCallContext context)
    {
        var oauthState = Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));
        var pairingId = Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));

        pairingStateStore.Add(new PairingState
        {
            PairingId = pairingId,
            PairingChallenge = request.PairingChallenge,
            OAuthState = oauthState,
            CreatedAt = DateTimeOffset.UtcNow
        });

        IEnumerable<string> scopes = request.Scopes.Count > 0 ? request.Scopes : (IEnumerable<string>)esiOptions.Scopes;
        return Task.FromResult(new StartPairingReply
        {
            PairingId = pairingId,
            AuthorizeUrl = BuildAuthorizeUrl(oauthState, scopes)
        });
    }

    public override async Task<RelayCodeReply> RelayCode(RelayCodeRequest request, ServerCallContext context)
    {
        var state = pairingStateStore.Get(request.PairingId);
        if (state is null)
            return new RelayCodeReply { Accepted = false, Message = "Unknown or expired pairing." };

        var (ok, message) = await pairingCompleter.CompleteAsync(state, request.Code, request.State, context.CancellationToken);
        return new RelayCodeReply { Accepted = ok, Message = message };
    }

    public override Task<ClaimPairingReply> ClaimPairing(ClaimPairingRequest request, ServerCallContext context)
    {
        var state = pairingStateStore.Get(request.PairingId);
        if (state is null)
            return Task.FromResult(new ClaimPairingReply { Completed = false, Message = "Unknown or expired pairing." });

        // Only the initiator (who holds the secret) can claim — guards against a leaked pairing_id.
        var computedChallenge = Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(request.PairingSecret)));
        if (!string.Equals(computedChallenge, state.PairingChallenge, StringComparison.Ordinal))
            return Task.FromResult(new ClaimPairingReply { Completed = false, Message = "Invalid pairing secret." });

        switch (state.Status)
        {
            case PairingStatus.Failed:
                return Task.FromResult(new ClaimPairingReply { Completed = false, Message = state.FailureMessage ?? "Pairing failed." });
            case PairingStatus.Pending:
                return Task.FromResult(new ClaimPairingReply { Completed = false, Message = "Pairing not completed yet." }); // client keeps polling
        }

        pairingStateStore.Remove(state.PairingId); // single-use
        return Task.FromResult(new ClaimPairingReply
        {
            Completed = true,
            SessionToken = state.SessionToken,
            SessionRefreshToken = state.SessionRefreshToken,
            CharacterId = state.CharacterId,
            CharacterName = state.CharacterName,
            ServerName = serverInfo.Name,
            CorporationName = state.CorporationName ?? string.Empty,
            AllianceName = state.AllianceName ?? string.Empty,
            Message = "ok"
        });
    }

    private string BuildAuthorizeUrl(string oauthState, IEnumerable<string> scopes)
    {
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = esiOptions.CallbackUri,
            ["client_id"] = esiOptions.ClientId,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = oauthState
        };

        var query = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{EsiEndpoints.Authorize}?{query}";
    }
}
