using System.Globalization;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.RunOutcomes;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Commands;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Queries;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using Grpc.Core;
using GrpcRunOutcomes = EveUtils.Grpc.RunOutcomes;
using WireLootLine = EveUtils.Grpc.RunOutcomeLootLine;

namespace EveUtils.Server.Grpc;

/// <summary>
/// The anonymous run outcomes of the earnings statistic (ET-514). The sharer is the authenticated session, never the
/// request; this is the only place that sees the character id and the activity's group code, and it hands the commands
/// their keyed pseudonyms instead (<see cref="RunOutcomePseudonyms"/>). Nothing it answers carries an id, pilot, bucket,
/// name or place. A refused session answers <see cref="StatusCode.Unauthenticated"/> like the other services; every
/// other refusal is a reply with a code.
/// </summary>
public sealed class RunOutcomesGrpcService(ServerSessionService sessions, RunOutcomePseudonyms pseudonyms, IDispatcher dispatcher)
    : GrpcRunOutcomes.RunOutcomesBase
{
    private const string DateFormat = "yyyy-MM-dd";

    public override async Task<RunOutcomeActionReply> ShareOutcome(ShareOutcomeRequest request, ServerCallContext context)
    {
        long characterId = await _CharacterAsync(context);
        if (string.IsNullOrWhiteSpace(request.ActivityRef))
            return _Refused(MessageCodes.ValidationFailed, "The outcome names no activity.");

        string activityId = pseudonyms.ActivityId(request.ActivityRef);
        string bucket = pseudonyms.ContributorBucket(characterId);
        if (request.Deleted)
            return _Reply(await dispatcher.Send(new RetractRunOutcomeCommand(activityId, bucket, request.Revision),
                context.CancellationToken));

        if (_Share(request) is not { } outcome)
            return _Refused(MessageCodes.ValidationFailed, "The outcome is not readable.");

        return _Reply(await dispatcher.Send(new ShareRunOutcomeCommand(activityId, bucket, request.Revision, outcome),
            context.CancellationToken));
    }

    public override async Task<RunOutcomeActionReply> WithdrawMyOutcomes(WithdrawMyOutcomesRequest request, ServerCallContext context)
    {
        long characterId = await _CharacterAsync(context);
        Result<int> withdrawn = await dispatcher.Send(
            new WithdrawRunOutcomesCommand(pseudonyms.ContributorBucket(characterId)), context.CancellationToken);
        return withdrawn.IsSuccess
            ? new RunOutcomeActionReply { Accepted = true, Message = "Outcomes withdrawn.", Count = withdrawn.Value }
            : _Refused(withdrawn);
    }

    public override async Task<GetOutcomesReply> GetOutcomes(GetOutcomesRequest request, ServerCallContext context)
    {
        await _CharacterAsync(context);
        if (!_TryDate(request.Since, out DateOnly since))
            return new GetOutcomesReply { Accepted = false, Code = MessageCodes.ValidationFailed, Message = "Invalid window." };

        Result<IReadOnlyList<ActivityOutcomesDto>> read = await dispatcher.Query(
            new GetRunOutcomesQuery([.. request.Keys.Select(_Key)], since), context.CancellationToken);
        if (!read.IsSuccess || read.Value is not { } activities)
            return new GetOutcomesReply { Accepted = false, Code = _Code(read), Message = _Text(read) };

        GetOutcomesReply reply = new() { Accepted = true };
        reply.Activities.AddRange(activities.Select(activity =>
        {
            ActivityOutcomes wire = new()
            {
                Key = _Wire(activity.Key), PilotCount = activity.PilotCount, Availability = (int)activity.Availability
            };
            wire.Outcomes.AddRange(activity.Outcomes.Select(outcome =>
            {
                RunOutcomeRow row = new()
                {
                    CompletedOn = outcome.CompletedOn.ToString(DateFormat, CultureInfo.InvariantCulture),
                    FixedIsk = outcome.FixedIsk.ToString(CultureInfo.InvariantCulture),
                    FleetSize = outcome.FleetSize,
                    LootBasis = (int)outcome.LootBasis,
                    OpenUnrecognisedLootCount = outcome.OpenUnrecognisedLootCount,
                    Weight = outcome.Weight
                };
                if (outcome.DurationSeconds is { } seconds)
                    row.DurationSeconds = seconds;
                return row;
            }));
            return wire;
        }));
        return reply;
    }

    public override async Task<GetOutcomeLootReply> GetOutcomeLoot(GetOutcomeLootRequest request, ServerCallContext context)
    {
        await _CharacterAsync(context);
        if (!_TryDate(request.Since, out DateOnly since) || request.Key is null)
            return new GetOutcomeLootReply { Accepted = false, Code = MessageCodes.ValidationFailed, Message = "Invalid request." };

        Result<ActivityOutcomeLootDto> read = await dispatcher.Query(
            new GetRunOutcomeLootQuery(_Key(request.Key), since), context.CancellationToken);
        if (!read.IsSuccess || read.Value is not { } loot)
            return new GetOutcomeLootReply { Accepted = false, Code = _Code(read), Message = _Text(read) };

        GetOutcomeLootReply reply = new() { Accepted = true, Availability = (int)loot.Availability, PilotCount = loot.PilotCount };
        reply.Outcomes.AddRange(loot.Outcomes.Select(outcome =>
        {
            RunOutcomeLoot wire = new()
            {
                CompletedOn = outcome.CompletedOn.ToString(DateFormat, CultureInfo.InvariantCulture),
                FleetSize = outcome.FleetSize,
                LootBasis = (int)outcome.LootBasis,
                Weight = outcome.Weight
            };
            wire.Lines.AddRange(outcome.Lines.Select(line =>
                new WireLootLine { Kind = (int)line.Kind, TypeId = line.TypeId, Quantity = line.Quantity }));
            return wire;
        }));
        return reply;
    }

    public override async Task<ListOutcomeKeysReply> ListOutcomeKeys(ListOutcomeKeysRequest request, ServerCallContext context)
    {
        await _CharacterAsync(context);
        if (!_TryDate(request.Since, out DateOnly since))
            return new ListOutcomeKeysReply { Accepted = false, Code = MessageCodes.ValidationFailed, Message = "Invalid window." };

        Result<IReadOnlyList<RunOutcomeKeyCountDto>> read = await dispatcher.Query(new ListRunOutcomeKeysQuery(since),
            context.CancellationToken);
        if (!read.IsSuccess || read.Value is not { } keys)
            return new ListOutcomeKeysReply { Accepted = false, Code = _Code(read), Message = _Text(read) };

        ListOutcomeKeysReply reply = new() { Accepted = true };
        reply.Keys.AddRange(keys.Select(key => new OutcomeKeyCount
        {
            Key = _Wire(key.Key),
            RunCount = key.RunCount,
            EffectiveRunCount = key.EffectiveRunCount,
            PilotCount = key.PilotCount
        }));
        return reply;
    }

    private static RunOutcomeShareDto? _Share(ShareOutcomeRequest request)
    {
        if (request.Key is null
            || !_TryDate(request.CompletedOn, out DateOnly completedOn)
            || !decimal.TryParse(request.FixedIsk, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal fixedIsk))
            return null;

        return new RunOutcomeShareDto
        {
            Key = _Key(request.Key),
            CompletedOn = completedOn,
            DurationSeconds = request.HasDurationSeconds ? request.DurationSeconds : null,
            FixedIsk = fixedIsk,
            FleetSize = request.FleetSize,
            LootBasis = (LootBasis)request.LootBasis,
            OpenUnrecognisedLootCount = request.OpenUnrecognisedLootCount,
            LootLines = [.. request.Loot.Select(line => new RunOutcomeLootLineDto
            {
                Kind = (RunOutcomeLootKind)line.Kind, TypeId = line.TypeId, Quantity = line.Quantity
            })]
        };
    }

    // Read as sent; the handlers normalise it and drop what names no activity.
    private static ActivityStatsKey _Key(RunOutcomeKey key) => new()
    {
        Kind = (ActivityStatsKind)key.Kind,
        SiteTypeSource = key.HasSiteTypeSource ? (SiteTypeSource)key.SiteTypeSource : null,
        TypeId = key.TypeId,
        Name = string.IsNullOrEmpty(key.Name) ? null : key.Name,
        MissionLevel = key.HasMissionLevel ? key.MissionLevel : null,
        AbyssalTier = key.HasAbyssalTier ? key.AbyssalTier : null,
        AbyssalWeather = string.IsNullOrEmpty(key.AbyssalWeather) ? null : key.AbyssalWeather
    };

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

    private static bool _TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static RunOutcomeActionReply _Reply(Result<RunOutcomeShareStatus> result) =>
        result.IsSuccess
            ? new RunOutcomeActionReply { Accepted = true, Message = result.Value.ToString(), Status = (int)result.Value }
            : _Refused(result);

    private static RunOutcomeActionReply _Refused(Result result) =>
        _Refused(_Code(result), _Text(result));

    private static RunOutcomeActionReply _Refused(string code, string message) =>
        new() { Accepted = false, Code = code, Message = message };

    private static string _Code(Result result) => result.Messages.FirstOrDefault()?.Code ?? MessageCodes.ServerError;

    private static string _Text(Result result) => result.Messages.FirstOrDefault()?.Text ?? "The request failed.";

    /// <summary>The acting character from the validated session, or <see cref="StatusCode.Unauthenticated"/> — the same
    /// signal the other services raise, so the client's refresh-and-retry recovers the call (ET-85).</summary>
    private async Task<long> _CharacterAsync(ServerCallContext context)
    {
        string? authorization = context.RequestHeaders.GetValue("authorization");
        string? token = authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : null;
        ServerSession? session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        return session?.SyncedCharacter?.EsiCharacterId is > 0 and var characterId
            ? characterId
            : throw new RpcException(new Status(StatusCode.Unauthenticated, "Not authenticated — pair with the server first."));
    }
}
