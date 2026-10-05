using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.Runs;

/// <summary>
/// "Start escalation run" (ET-451): one click from a registered escalation to a running run — type Site, the
/// escalation's own site and system, the source run's character, and the link back to the source run that ticks the
/// escalation off there once this run is saved. The one route the open-escalations band and the detail screen share.
///
/// The character is the source run's, not a picker: the escalation was offered to that pilot, and nobody else can
/// fly it. A run for an escalation the pilot never registered still starts the ordinary way, through the manual start.
/// </summary>
internal sealed class EscalationRunStarter(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services)
{
    /// <summary>Starts the run and opens its window, or returns why it did not.</summary>
    /// <param name="sourceRunId">The run the escalation was registered on.</param>
    /// <param name="characterId">That run's character — the pilot who flies the escalation.</param>
    /// <param name="characterName">Their name, for the run's snapshot and the window.</param>
    /// <param name="escalation">The escalation to fly.</param>
    public async Task<string?> StartAsync(
        Guid sourceRunId, long characterId, string characterName, RunEscalationDto escalation)
    {
        Result<IReadOnlyList<OpenEscalationDto>> open =
            await dispatcher.Query(new GetOpenEscalationsQuery(new HashSet<long> { characterId }));
        if (open.Value?.Any(row => row.SourceRunId == sourceRunId && row.Escalation.EntryId == escalation.EntryId
                && row.InProgressRunId is not null) == true)
            return "An escalation run for this escalation is already under way.";

        Result<IReadOnlyList<RunningRunDto>> running = await dispatcher.Query(new GetRunningRunsQuery());
        if (running.Value?.Any(run => run.CharacterId == characterId) == true)
            return $"{characterName} is already flying a run. Stop and save it first.";

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(
            characterId, ActivityKind.Site, DateTime.UtcNow, escalation.DungeonId ?? 0, escalation.SiteName,
            escalation.SolarSystemId, SolarSystemName: escalation.SystemName, CharacterNameSnapshot: characterName,
            SiteTypeSource: SiteTypeSource.Site, Origin: RunOrigin.Manual,
            Parameters:
            [
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.EscalationSourceRunId,
                    TypedValue = sourceRunId.ToString(),
                    EntryId = escalation.EntryId,
                    ObservedAtUtc = DateTime.UtcNow
                }
            ]));
        if (!started.IsSuccess)
            return started.Messages.Count > 0 ? started.Messages[0].Text : "The escalation run could not be started.";

        // Named before the window loads, the same reason ManualRunStartViewModel names its pilot (ET-221): the window
        // adopts this character's running run instead of looking for "the one run running".
        var window = new ActivityWindowViewModel(ActivityKind.Site, services);
        window.UseCharacter(checked((int)characterId), characterName);
        dialogs.ShowActivityWindow(window);
        return null;
    }
}
