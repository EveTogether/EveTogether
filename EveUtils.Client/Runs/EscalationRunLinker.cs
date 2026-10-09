using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.Runs;

/// <summary>
/// "Link to a run…" (ET-489): an escalation flown as an ordinary site run, not through START, is tied to that run
/// afterwards — the same Completed-with-a-link a START run ends in. The one route OPEN ESCALATIONS and the detail
/// screen share.
/// </summary>
internal sealed class EscalationRunLinker(CqrsDispatcher dispatcher, IDialogService dialogs, Func<long, string> nameOf)
{
    /// <summary>Offers the plausible runs and links the one picked, or returns why nothing happened. Null when linked
    /// or when the pilot cancelled.</summary>
    public async Task<string?> LinkAsync(Guid sourceRunId, RunEscalationDto escalation)
    {
        Result<IReadOnlyList<LinkableRunDto>> found =
            await dispatcher.Query(new GetLinkableRunsQuery(sourceRunId, escalation.EntryId));
        if (found.Value is not { Count: > 0 } candidates)
            return "No saved run of this site since the escalation was registered can be linked.";

        Guid? chosen = await dialogs.PickRunAsync(
            $"Which run did you fly {OpsecText.Mark(escalation.SiteName) ?? escalation.SiteName} in?",
            [.. candidates.Select(_Option)]);
        if (chosen is not { } runId)
            return null;

        Result linked = await dispatcher.Send(new SetEscalationOutcomeCommand(
            sourceRunId, escalation.EntryId, EscalationOutcome.Completed, runId));
        return linked.IsSuccess
            ? null
            : linked.Messages.Count > 0 ? linked.Messages[0].Text : "The escalation could not be linked.";
    }

    private RunPickOption _Option(LinkableRunDto run) => new(run.RunId,
        OpsecText.Mark(run.SiteName) ?? "a site run",
        $"{run.StartedAtUtc.ToLocalTime():d MMM HH:mm} · {CharacterNameResolver.Resolve(run.CharacterNameSnapshot, run.CharacterId, nameOf)}" +
        $" · {_SystemText(run)}");

    private static string _SystemText(LinkableRunDto run) => run switch
    {
        { IsSameSystem: true } => "in the escalation's system",
        { SolarSystemId: null } => "no system recorded",
        _ => "in another system"
    };
}
