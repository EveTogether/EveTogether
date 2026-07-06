using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Grpc;
using Grpc.Core;
using GrpcFittings = EveUtils.Grpc.Fittings;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.Transport;

/// <summary>
/// Calls the server's fittings RPCs over the TOFU-pinned channel, using a stored server session as the bearer.
/// Returns the server's real accept/deny result so the UI shows the truth.
///
/// Multi-character: each call takes an optional <c>actingCharacterId</c> so the caller can act as a specific
/// coupled character (e.g. the "shared by" identity), defaulting to the most-recent session.
///
/// Token refresh: runs through <see cref="InvokeAsync{TReply}"/>, which on an Unauthenticated reply
/// refreshes the session once and retries — so sharing doesn't fail "Not authenticated" after the 1-hour access token
/// expires under a still-open event-bus stream.
/// </summary>
public sealed class ServerFitShareClient(
    GrpcChannelFactory channelFactory, IClientSessionStore sessionStore, ServerSessionRefresher refresher) : ISingletonService
{
    public async Task<(bool Accepted, string Message)> ShareAsync(
        string serverAddress, int esiFittingId, string name, int shipTypeId, string rawJson,
        int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, (client, headers, session) =>
                // "Shared by" = the acting character (the session), not the fit's source (fits are local + shared).
                client.ShareFitAsync(new ShareFitRequest
                {
                    EsiFittingId = esiFittingId,
                    Name = name,
                    ShipTypeId = shipTypeId,
                    RawJson = rawJson,
                    SharedByCharacterName = session.CharacterName,
                    SharedByCharacterId = session.CharacterId
                }, headers, cancellationToken: cancellationToken), cancellationToken);
            return (reply.Accepted, reply.Message);
        }
        catch (RpcException ex)
        {
            return (false, $"Share failed: {ex.Status.Detail}");
        }
    }

    /// <summary>Fetches the fits available on the server, for browsing + downloading to local.</summary>
    public async Task<(bool Ok, string Message, IReadOnlyList<SharedFitInfo> Fits)> GetSharedFitsAsync(
        string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, (client, headers, _) =>
                client.GetSharedFitsAsync(new GetSharedFitsRequest(), headers, cancellationToken: cancellationToken), cancellationToken);
            var fits = reply.Fits
                .Select(f => new SharedFitInfo(f.Id, f.EsiFittingId, f.Name, f.ShipTypeId, f.RawJson, f.SharedByCharacterName, f.SharedByCharacterId,
                    DateTimeOffset.TryParse(f.SharedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sharedAt) ? sharedAt : default))
                .ToList();
            return (reply.Ok, reply.Message, fits);
        }
        catch (RpcException ex)
        {
            return (false, $"Fetch failed: {ex.Status.Detail}", []);
        }
    }

    /// <summary>Deletes a fit from the server's shared library by its server id.</summary>
    public async Task<(bool Accepted, string Message)> DeleteSharedFitAsync(
        string serverAddress, int serverFitId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, (client, headers, _) =>
                client.DeleteSharedFitAsync(new DeleteSharedFitRequest { Id = serverFitId }, headers, cancellationToken: cancellationToken), cancellationToken);
            return (reply.Accepted, reply.Message);
        }
        catch (RpcException ex)
        {
            return (false, $"Delete failed: {ex.Status.Detail}");
        }
    }

    private const string NotPaired = "Not authenticated — pair with the server first.";

    /// <summary>Runs one unary RPC with the acting character's bearer; on Unauthenticated refreshes the session once
    /// and retries. The rpc receives the active session so it can stamp "shared by".</summary>
    private async Task<TReply> InvokeAsync<TReply>(
        string serverAddress, int actingCharacterId,
        Func<GrpcFittings.FittingsClient, Metadata, ClientSessionTokens, AsyncUnaryCall<TReply>> rpc, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(serverAddress, actingCharacterId, cancellationToken);
        if (session is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, NotPaired));

        var channel = channelFactory.CreatePinned(serverAddress);
        var client = new GrpcFittings.FittingsClient(channel);
        try
        {
            return await rpc(client, BearerHeaders(session.AccessToken), session);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            var refreshed = await refresher.RefreshAsync(serverAddress, actingCharacterId, cancellationToken);
            if (refreshed is null)
                throw;
            return await rpc(client, BearerHeaders(refreshed.AccessToken), refreshed); // retry once with the rotated token
        }
    }

    private Task<ClientSessionTokens?> LoadSessionAsync(string serverAddress, int actingCharacterId, CancellationToken cancellationToken) =>
        actingCharacterId != 0
            ? sessionStore.LoadForCharacterAsync(serverAddress, actingCharacterId, cancellationToken)
            : sessionStore.LoadAsync(serverAddress, cancellationToken);

    private static Metadata BearerHeaders(string accessToken) => new() { { "authorization", $"Bearer {accessToken}" } };
}
