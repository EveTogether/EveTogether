using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Grouping;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class StartRunCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<StartRunCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(StartRunCommand command, CancellationToken cancellationToken = default)
    {
        if (command.CharacterId <= 0)
            return Result<Guid>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A run requires a character.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Guid id = Guid.CreateVersion7();
        string? groupCode = command.GroupCode ?? _CreateGroupCode(command);
        db.Set<Run>().Add(new Run
        {
            Id = id,
            CharacterId = command.CharacterId,
            GroupCode = groupCode,
            ActivityKind = command.ActivityKind,
            State = RunState.Running,
            StartedAtUtc = command.StartedAtUtc,
            SiteTypeId = command.SiteTypeId,
            SiteTypeSource = command.SiteTypeSource,
            SiteName = command.SiteName,
            SolarSystemId = command.SolarSystemId,
            Signature = command.Signature,
            AgentId = command.AgentId,
            MissionLevel = command.MissionLevel,
            Role = command.Role,
            IsParticipant = command.IsParticipant,
            IsPayoutEligible = command.IsPayoutEligible,
            FitContentHash = command.FitContentHash,
            FitNameSnapshot = command.FitNameSnapshot,
            Origin = command.Origin,
            SyncState = RunSyncState.Local,
            Revision = 1
        });
        foreach (RunParameterInput parameter in command.Parameters ?? [])
            db.Set<RunParameter>().Add(new RunParameter
            {
                Id = Guid.CreateVersion7(),
                RunId = id,
                ParameterKey = parameter.ParameterKey,
                TypedValue = parameter.TypedValue,
                Amount = parameter.Amount,
                ItemTypeId = parameter.ItemTypeId,
                BonusWindowSeconds = parameter.BonusWindowSeconds,
                ObservedAtUtc = parameter.ObservedAtUtc
            });
        // The only moment this handler is sure both facts at once: a code minted here (the FC's own start), or one
        // already handed to this run by the fleet (a member starting on an offered code). Either way, the group
        // code's fleet is known now and would not be if this were left to be inferred later (ET-182).
        if (groupCode is not null && command.FleetId is { } originFleetId)
            await RunGroupOriginRecorder.RecordAsync(db, groupCode, originFleetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunStartedEvent(id, command.CharacterId, command.ActivityKind, command.StartedAtUtc,
            command.FleetId, groupCode, command.IsFleetCommander, command.SolarSystemName, command.SiteName),
            EventTarget.Local, cancellationToken);
        if (command.FleetId is { } fleetId && groupCode is not null)
            await eventBus.PublishAsync(new FleetRunGroupCodeEvent(new RunGroupCodeStart(fleetId, command.ActivityKind,
                groupCode, command.StartedAtUtc, command.IsFleetCommander, command.SiteName, command.SolarSystemName,
                command.Signature),
                checked((int)command.CharacterId)), EventTarget.Both, cancellationToken);
        return Result<Guid>.Success(id);
    }

    private static string? _CreateGroupCode(StartRunCommand command) => command.FleetId is null
        || RunGroupCodeArbiter.TakesGroupFromCommanderOnly(command.ActivityKind) && !command.IsFleetCommander
            ? null
            : RunGroupCode.Create();
}
