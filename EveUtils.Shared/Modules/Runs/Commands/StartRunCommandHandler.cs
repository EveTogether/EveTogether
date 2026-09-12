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

        string? groupCode = command.GroupCode ?? _CreateGroupCode(command);
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // I7 (ET-274): the run this character already has in the group IS this start's run — the attendance backfill,
        // another window or an earlier start filed it a moment ago — never a second row beside it. HF-DYB4: the backfill
        // made the four siblings 187 ms after the pilot's run, and the multi-pick start filed four more 2.7 s later.
        if (groupCode is not null && await OneRunPerCharacter.FindAsync(db, groupCode, command.CharacterId, cancellationToken)
                is { } existing)
            return await _TakeOverAsync(db, existing, command, cancellationToken);

        Guid id = Guid.CreateVersion7();
        (HomefrontOutcome? outcome, int? waves) = command.SiteTypeSource is SiteTypeSource.Site
            ? HomefrontCatalogue.DefaultOutcomeFor(command.SiteTypeId)
            : (null, null);
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
            CharacterNameSnapshot = command.CharacterNameSnapshot,
            SignatureGroupSnapshot = command.SignatureGroupSnapshot,
            Origin = command.Origin,
            HomefrontOutcome = outcome,
            HomefrontCompletedWaveCount = waves,
            SyncState = RunSyncState.Local,
            Revision = 1
        });
        _AddParameters(db, id, command);
        // The only moment this handler is sure both facts at once: a code minted here (the FC's own start), or one
        // already handed to this run by the fleet (a member starting on an offered code). Either way, the group
        // code's fleet is known now and would not be if this were left to be inferred later (ET-182).
        if (groupCode is not null && command.FleetId is { } originFleetId)
            await RunGroupOriginRecorder.RecordAsync(db, groupCode, originFleetId, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (groupCode is not null)
        {
            // Two paths filed this character in the same breath and the index let one through: that one is the run.
            await using ClientDbContext fresh = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (await OneRunPerCharacter.FindAsync(fresh, groupCode, command.CharacterId, cancellationToken) is not { } filed)
                throw;
            return await _TakeOverAsync(fresh, filed, command, cancellationToken);
        }

        await _AnnounceAsync(id, groupCode, command, cancellationToken);
        return Result<Guid>.Success(id);
    }

    /// <summary>
    /// This character's run in the group already exists, so the start takes it over and fills in what only a start
    /// knows — the fit, the name, a mission's reward lines — where the run has none. Started at the same moment it is
    /// this character's start and is announced as one (the tallies, the coordinator and the fleet hear of it exactly
    /// as they would of a fresh row); a start on a run already going is no new start and says nothing. A run already
    /// saved is not started again.
    /// </summary>
    private async Task<Result<Guid>> _TakeOverAsync(ClientDbContext db, Run run, StartRunCommand command,
        CancellationToken cancellationToken)
    {
        if (run.State is RunState.Saved)
            return Result<Guid>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.Duplicate,
                $"{command.CharacterNameSnapshot ?? "This character"} already has a saved run in this activity.", "Runs"));

        bool isChanged = false;
        if (run.FitContentHash is null && command.FitContentHash is not null)
        {
            run.FitContentHash = command.FitContentHash;
            run.FitNameSnapshot = command.FitNameSnapshot;
            isChanged = true;
        }
        if (run.CharacterNameSnapshot is null && command.CharacterNameSnapshot is not null)
        {
            run.CharacterNameSnapshot = command.CharacterNameSnapshot;
            isChanged = true;
        }
        if (run.SiteTypeId == 0 && command.SiteTypeId != 0)
        {
            run.SiteTypeId = command.SiteTypeId;
            run.SiteTypeSource = command.SiteTypeSource;
            isChanged = true;
        }
        if (command.Parameters is { Count: > 0 }
            && !await db.Set<RunParameter>().AnyAsync(parameter => parameter.RunId == run.Id, cancellationToken))
        {
            _AddParameters(db, run.Id, command);
            isChanged = true;
        }
        if (isChanged)
        {
            run.Revision++;
            if (run.SyncState is RunSyncState.Synced)
                run.SyncState = RunSyncState.Outdated;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (run.State is RunState.Running && run.StartedAtUtc == command.StartedAtUtc)
            await _AnnounceAsync(run.Id, run.GroupCode, command, cancellationToken);
        else if (isChanged)
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result<Guid>.Success(run.Id);
    }

    private async Task _AnnounceAsync(Guid id, string? groupCode, StartRunCommand command, CancellationToken cancellationToken)
    {
        await eventBus.PublishAsync(new RunStartedEvent(id, command.CharacterId, command.ActivityKind, command.StartedAtUtc,
            command.FleetId, groupCode, command.IsFleetCommander, command.SolarSystemName, command.SiteName),
            EventTarget.Local, cancellationToken);
        if (command.FleetId is { } fleetId && groupCode is not null)
            await eventBus.PublishAsync(new FleetRunGroupCodeEvent(new RunGroupCodeStart(fleetId, command.ActivityKind,
                groupCode, command.StartedAtUtc, command.IsFleetCommander, command.SiteName, command.SolarSystemName,
                command.Signature, command.SignatureGroupSnapshot, command.AbyssalTierIndex, command.AbyssalWeatherName,
                command.SiteTypeId),
                checked((int)command.CharacterId)), EventTarget.Both, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(id, groupCode), EventTarget.Local, cancellationToken);
    }

    private static void _AddParameters(ClientDbContext db, Guid runId, StartRunCommand command)
    {
        foreach (RunParameterInput parameter in command.Parameters ?? [])
            db.Set<RunParameter>().Add(new RunParameter
            {
                Id = Guid.CreateVersion7(),
                RunId = runId,
                ParameterKey = parameter.ParameterKey,
                TypedValue = parameter.TypedValue,
                Amount = parameter.Amount,
                ItemTypeId = parameter.ItemTypeId,
                BonusWindowSeconds = parameter.BonusWindowSeconds,
                ObservedAtUtc = parameter.ObservedAtUtc
            });
    }

    private static string? _CreateGroupCode(StartRunCommand command) => command.FleetId is null
        || RunGroupCodeArbiter.TakesGroupFromCommanderOnly(command.ActivityKind) && !command.IsFleetCommander
            ? null
            : RunGroupCode.Create();
}
