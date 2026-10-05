using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.App;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Runtime;
using EveUtils.Shared.Transport;
using GrpcPairing = EveUtils.Grpc.Pairing;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Pairing;

/// <summary>
/// Client-side Mode B pairing (server-redirect flow, Auth-Flow B1). The server has its own ESI app +
/// callback, so the SSO redirect lands on the server and the server completes the exchange itself —
/// the client only proves it is the initiator (pairing secret; only its hash leaves the client), pins
/// the server cert on first contact (TOFU), opens the browser, and polls until the server issues a session.
/// </summary>
public sealed class ServerPairingService(
    GrpcChannelFactory channelFactory,
    IServerTrustStore trustStore,
    IClientSessionStore sessionStore) : ISingletonService
{
    /// <summary>
    /// Queries the server's declared scopes so the client can show an opt-in popup for the
    /// optional ones before pairing. Uses a permissive HTTP probe — the real cert is verified via TOFU
    /// during the gRPC pairing that follows.
    /// </summary>
    public async Task<ServerScopesResponse?> GetServerScopesAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(serverAddress) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent(ExecutionHost.Client));
            return await http.GetFromJsonAsync<ServerScopesResponse>("/api/server/scopes", cancellationToken);
        }
        catch (Exception)
        {
            return null; // best-effort: pairing still works without the optional-scope popup
        }
    }

    /// <param name="expectedCharacterId">The character this coupling is for; the server refuses any other one that signs
    /// in on the EVE page (ET-425). 0 couples whoever signs in.</param>
    /// <param name="authorizeUrl">Receives the EVE login link, for a browser that did not open by itself.</param>
    /// <exception cref="PairingFailedException">The server refused or the EVE login was declined.</exception>
    public async Task<PairingResult> PairAsync(
        string serverAddress,
        IReadOnlyList<string>? scopes = null,
        Action<string>? status = null,
        int expectedCharacterId = 0,
        Action<string>? authorizeUrl = null,
        CancellationToken cancellationToken = default)
    {
        var pairingSecret = TokenSecurity.GenerateToken();
        var pairingChallenge = Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pairingSecret)));

        status?.Invoke("Contacting server…");
        using var pinning = channelFactory.CreateForPairing(serverAddress);
        var client = new GrpcPairing.PairingClient(pinning.Channel);

        var startRequest = new StartPairingRequest { PairingChallenge = pairingChallenge, ExpectedCharacterId = expectedCharacterId };
        if (scopes is not null)
            startRequest.Scopes.AddRange(scopes); // server includes these in the authorize URL

        var start = await client.StartPairingAsync(startRequest, cancellationToken: cancellationToken);

        // TOFU: pin the cert presented on this first contact.
        var fingerprint = pinning.PresentedFingerprint();
        if (fingerprint is not null)
            trustStore.Pin(serverAddress, fingerprint);

        status?.Invoke("Opening browser for EVE SSO…");
        authorizeUrl?.Invoke(start.AuthorizeUrl);
        OpenBrowser(start.AuthorizeUrl); // redirect lands on the server's own callback; the server completes the exchange

        status?.Invoke("Waiting for the server to complete pairing…");
        ClaimPairingReply claim;
        while (true)
        {
            claim = await client.ClaimPairingAsync(
                new ClaimPairingRequest { PairingId = start.PairingId, PairingSecret = pairingSecret },
                cancellationToken: cancellationToken);
            if (claim.Completed)
                break;
            if (!string.Equals(claim.Message, "Pairing not completed yet.", StringComparison.Ordinal))
                throw new PairingFailedException(claim.Failure, claim.CharacterName, claim.Message);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        // A server from before ET-425 ignores the expected character, so the same rule is held here as well: the session
        // it issued for someone else is never saved, and nothing on this PC gets coupled.
        if (expectedCharacterId != 0 && claim.CharacterId != expectedCharacterId)
            throw new PairingFailedException(PairingFailure.OtherCharacter, claim.CharacterName,
                $"{claim.CharacterName} signed in, but this coupling is for another character. Nothing was coupled.");

        await sessionStore.SaveAsync(
            serverAddress,
            // The session id comes with the pairing, so a client that has just coupled can name its session from the
            // first second. Coupling again is the way out of "your session is gone", and that recovery should not
            // start with a window in which the same refusal is unclassifiable all over again.
            new ClientSessionTokens(
                claim.SessionToken, claim.SessionRefreshToken, claim.CharacterName, claim.CharacterId, claim.SessionId),
            cancellationToken);

        return new PairingResult(
            claim.CharacterName, claim.CharacterId, claim.ServerName, claim.CorporationName, claim.AllianceName);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Console.WriteLine($"Open this URL to authorize:\n{url}");
        }
    }
}
