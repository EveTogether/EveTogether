using System.Globalization;
using EveUtils.Grpc;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
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

    public Task<OutcomeReadResult<ActivityOutcomesDto>> GetOutcomesAsync(string serverAddress, long characterId,
        ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default)
    {
        GetOutcomesRequest request = new() { Since = since.ToString(DateFormat, CultureInfo.InvariantCulture) };
        request.Keys.Add(_Wire(key));
        return _ReadAsync(serverAddress, characterId,
            (client, headers) => client.GetOutcomesAsync(request, headers, cancellationToken: cancellationToken),
            reply => (reply.Accepted, reply.Code, reply.Message,
                reply.Activities.FirstOrDefault() is { } activity ? FromWire(key, activity) : null),
            cancellationToken);
    }

    public Task<OutcomeReadResult<ActivityOutcomeLootDto>> GetOutcomeLootAsync(string serverAddress, long characterId,
        ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default) =>
        _ReadAsync(serverAddress, characterId,
            (client, headers) => client.GetOutcomeLootAsync(
                new GetOutcomeLootRequest { Key = _Wire(key), Since = since.ToString(DateFormat, CultureInfo.InvariantCulture) },
                headers, cancellationToken: cancellationToken),
            reply => (reply.Accepted, reply.Code, reply.Message, FromWire(reply)),
            cancellationToken);

    internal static ActivityOutcomesDto FromWire(ActivityStatsKey key, ActivityOutcomes activity) => new()
    {
        Key = key,
        Availability = (SharedOutcomesAvailability)activity.Availability,
        PilotCount = activity.PilotCount,
        Outcomes = [.. activity.Outcomes.Select(row => new RunOutcomeDto
        {
            CompletedOn = _Day(row.CompletedOn),
            DurationSeconds = row.HasDurationSeconds ? row.DurationSeconds : null,
            FixedIsk = decimal.Parse(row.FixedIsk, CultureInfo.InvariantCulture),
            FleetSize = row.FleetSize,
            LootBasis = (LootBasis)row.LootBasis,
            OpenUnrecognisedLootCount = row.OpenUnrecognisedLootCount,
            Weight = row.Weight
        })]
    };

    internal static ActivityOutcomeLootDto FromWire(GetOutcomeLootReply reply) => new()
    {
        Availability = (SharedOutcomesAvailability)reply.Availability,
        PilotCount = reply.PilotCount,
        Outcomes = [.. reply.Outcomes.Select(outcome => new RunOutcomeLootDto
        {
            CompletedOn = _Day(outcome.CompletedOn),
            FleetSize = outcome.FleetSize,
            LootBasis = (LootBasis)outcome.LootBasis,
            Weight = outcome.Weight,
            Lines = [.. outcome.Lines.Select(line => new RunOutcomeLootLineDto
            {
                Kind = (RunOutcomeLootKind)line.Kind, TypeId = line.TypeId, Quantity = line.Quantity
            })]
        })]
    };

    private static DateOnly _Day(string isoDay) => DateOnly.ParseExact(isoDay, DateFormat, CultureInfo.InvariantCulture);

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

    private async Task<OutcomeReadResult<T>> _ReadAsync<TReply, T>(string serverAddress, long characterId,
        Func<GrpcRunOutcomes.RunOutcomesClient, Metadata, AsyncUnaryCall<TReply>> rpc,
        Func<TReply, (bool Accepted, string Code, string Message, T? Value)> read, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            (bool accepted, string code, string message, T? value) = read(
                await _InvokeAsync(serverAddress, characterId, rpc, cancellationToken));
            return accepted && value is not null
                ? new OutcomeReadResult<T>(value, string.Empty, string.Empty)
                : new OutcomeReadResult<T>(null, code, message);
        }
        catch (RpcException exception)
        {
            return OutcomeReadResult<T>.Transient($"Could not reach the server: {exception.Status.Detail}");
        }
        catch (InvalidOperationException exception)
        {
            return OutcomeReadResult<T>.Transient(exception.Message);
        }
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

    private async Task<TReply> _InvokeAsync<TReply>(string serverAddress, long characterId,
        Func<GrpcRunOutcomes.RunOutcomesClient, Metadata, AsyncUnaryCall<TReply>> rpc,
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
