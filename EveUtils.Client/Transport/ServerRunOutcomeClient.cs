using System.Globalization;
using EveUtils.Grpc;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Transport;
using Grpc.Core;
using GrpcRunOutcomes = EveUtils.Grpc.RunOutcomes;

namespace EveUtils.Client.Transport;

public sealed class ServerRunOutcomeClient(
    GrpcChannelFactory channelFactory, IClientSessionStore sessionStore, ServerSessionRefresher refresher)
    : IServerRunOutcomeClient, ISingletonService
{
    private const string DateFormat = "yyyy-MM-dd";

    public Task<OutcomeCallResult> ShareAsync(string serverAddress, long characterId, string activityRef, long revision,
        RunOutcomeShareDto? outcome, CancellationToken cancellationToken = default) =>
        _CallAsync(serverAddress, characterId, (client, headers) =>
            client.ShareOutcomeAsync(ToRequest(activityRef, revision, outcome), headers, cancellationToken: cancellationToken),
            cancellationToken);

    public Task<OutcomeCallResult> WithdrawAsync(string serverAddress, long characterId,
        CancellationToken cancellationToken = default) =>
        _CallAsync(serverAddress, characterId, (client, headers) =>
            client.WithdrawMyOutcomesAsync(new WithdrawMyOutcomesRequest(), headers, cancellationToken: cancellationToken),
            cancellationToken);

    /// <summary>Everything that leaves the machine, field by field. Nothing here reads a system, signature, fit, name or
    /// time of day: they are not on <see cref="RunOutcomeShareDto"/> to begin with.</summary>
    internal static ShareOutcomeRequest ToRequest(string activityRef, long revision, RunOutcomeShareDto? outcome)
    {
        ShareOutcomeRequest request = new() { ActivityRef = activityRef, Revision = revision, Deleted = outcome is null };
        if (outcome is null)
            return request;

        request.Key = _Wire(outcome.Key);
        request.CompletedOn = outcome.CompletedOn.ToString(DateFormat, CultureInfo.InvariantCulture);
        request.FixedIsk = outcome.FixedIsk.ToString(CultureInfo.InvariantCulture);
        request.FleetSize = outcome.FleetSize;
        request.LootBasis = (int)outcome.LootBasis;
        request.OpenUnrecognisedLootCount = outcome.OpenUnrecognisedLootCount;
        if (outcome.DurationSeconds is { } seconds)
            request.DurationSeconds = seconds;
        request.Loot.AddRange(outcome.LootLines.Select(line =>
            new EveUtils.Grpc.RunOutcomeLootLine { Kind = (int)line.Kind, TypeId = line.TypeId, Quantity = line.Quantity }));
        return request;
    }

    private static RunOutcomeKey _Wire(ActivityStatsKey key)
    {
        RunOutcomeKey wire = new()
        {
            Kind = (int)key.Kind, TypeId = key.TypeId, Name = key.Name ?? "", AbyssalWeather = key.AbyssalWeather ?? ""
        };
        if (key.SiteTypeSource is { } source)
            wire.SiteTypeSource = (int)source;
        if (key.MissionLevel is { } level)
            wire.MissionLevel = level;
        if (key.AbyssalTier is { } tier)
            wire.AbyssalTier = tier;
        return wire;
    }

    private async Task<OutcomeCallResult> _CallAsync(string serverAddress, long characterId,
        Func<GrpcRunOutcomes.RunOutcomesClient, Metadata, AsyncUnaryCall<RunOutcomeActionReply>> rpc,
        CancellationToken cancellationToken)
    {
        try
        {
            RunOutcomeActionReply reply = await _InvokeAsync(serverAddress, characterId, rpc, cancellationToken);
            return new OutcomeCallResult(reply.Accepted, reply.Code, reply.Message,
                reply.Accepted && reply.Status >= 0 ? (RunOutcomeShareStatus)reply.Status : null, reply.Count);
        }
        catch (RpcException exception)
        {
            return OutcomeCallResult.Transient($"Could not reach the server: {exception.Status.Detail}");
        }
        catch (InvalidOperationException exception)
        {
            return OutcomeCallResult.Transient(exception.Message);
        }
    }

    private async Task<RunOutcomeActionReply> _InvokeAsync(string serverAddress, long characterId,
        Func<GrpcRunOutcomes.RunOutcomesClient, Metadata, AsyncUnaryCall<RunOutcomeActionReply>> rpc,
        CancellationToken cancellationToken)
    {
        if (characterId is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException("The character id cannot be paired with a server session.");

        int sessionCharacterId = (int)characterId;
        ClientSessionTokens session = await sessionStore.LoadForCharacterAsync(serverAddress, sessionCharacterId, cancellationToken)
                                      ?? throw new InvalidOperationException("Not paired with the server for this character.");
        GrpcRunOutcomes.RunOutcomesClient client = new(channelFactory.CreatePinned(serverAddress));
        try
        {
            return await rpc(client, _BearerHeaders(session.AccessToken));
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unauthenticated)
        {
            ClientSessionTokens? refreshed = await refresher.RefreshAsync(serverAddress, sessionCharacterId, cancellationToken);
            if (refreshed is null)
                throw;
            return await rpc(client, _BearerHeaders(refreshed.AccessToken));
        }
    }

    private static Metadata _BearerHeaders(string accessToken) => new() { { "authorization", $"Bearer {accessToken}" } };
}
