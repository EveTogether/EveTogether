using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>What a MINING line's edit action writes through (ET-424), the same commands on a running run and a saved
/// one. Each answer is the refusal's own text, or null once the correction was stored — what a row says under its box.
/// <paramref name="afterCorrected"/> is for the screen that has to read what the correction moved; the run window reads
/// its rows again on its own clock.</summary>
public sealed class MiningLineCorrector(CqrsDispatcher dispatcher, Func<Task>? afterCorrected = null)
{
    public async Task<string?> SetUnitsAsync(Guid runId, string oreType, int units, CancellationToken cancellationToken = default) =>
        await _AfterAsync(await dispatcher.Send(new SetRunMiningEntryUnitsCommand(runId, oreType, units), cancellationToken));

    public async Task<string?> RemoveAsync(Guid runId, string oreType, CancellationToken cancellationToken = default) =>
        await _AfterAsync(await dispatcher.Send(new RemoveRunMiningEntryCommand(runId, oreType), cancellationToken));

    private async Task<string?> _AfterAsync(Result result)
    {
        if (!result.IsSuccess)
            return result.Messages.Count > 0 ? result.Messages[0].Text : "This mining line could not be changed.";

        if (afterCorrected is not null)
            await afterCorrected();
        return null;
    }
}
