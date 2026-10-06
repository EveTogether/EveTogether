using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>The link between an escalation run and the escalation it flew (ET-453): deleting the run takes the tick-off
/// back, so the source never keeps a completion pointing at a run that is gone.</summary>
internal static class EscalationRunLinks
{
    /// <summary>Reopens every escalation the deleted runs had ticked off. One the pilot settled some other way since —
    /// marked by hand, declined, or completed by another run — is left alone.</summary>
    public static async Task ReopenSourcesOfAsync(
        ClientDbContext db, IDispatcher dispatcher, IReadOnlyCollection<Guid> deletedRunIds, CancellationToken cancellationToken)
    {
        var links = await db.Set<RunParameter>().AsNoTracking()
            .Where(parameter => deletedRunIds.Contains(parameter.RunId)
                && parameter.ParameterKey == RunParameterKey.EscalationSourceRunId)
            .Select(parameter => new { parameter.RunId, parameter.TypedValue, parameter.EntryId })
            .ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            if (!Guid.TryParse(link.TypedValue, out Guid sourceRunId))
                continue;

            string completedBy = link.RunId.ToString();
            bool tickedOffByThisRun = await db.Set<RunParameter>().AsNoTracking().AnyAsync(parameter =>
                parameter.RunId == sourceRunId && parameter.EntryId == link.EntryId
                && parameter.ParameterKey == RunParameterKey.EscalationCompletedByRunId
                && parameter.TypedValue == completedBy, cancellationToken);
            if (tickedOffByThisRun)
                await dispatcher.Send(new SetEscalationOutcomeCommand(sourceRunId, link.EntryId, null), cancellationToken);
        }
    }
}
