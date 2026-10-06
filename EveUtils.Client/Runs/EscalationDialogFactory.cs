using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.Runs;

/// <summary>Builds the register-escalation dialog for a source site (ET-451), the one way the run window and the detail
/// screen (ET-453) both open it.</summary>
internal static class EscalationDialogFactory
{
    /// <summary>Ranks first the escalations registered before from the source site — none while the site matched nothing
    /// or matched more than one site.</summary>
    public static async Task<EscalationDialogViewModel> CreateAsync(
        ISdeAccessor sde, IReadOnlyList<SdeSite> sourceSites, CqrsDispatcher dispatcher)
    {
        if (SdeSiteCanonicalization.Canonicalize(sourceSites) is not [{ DungeonId: var sourceDungeonId }])
            return new EscalationDialogViewModel(sde, sourceSites, []);

        Result<IReadOnlyList<int>> history = await dispatcher.Query(new GetEscalationHistoryQuery(sourceDungeonId));
        return new EscalationDialogViewModel(sde, sourceSites, history.Value ?? []);
    }
}
