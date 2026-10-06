using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>The one way unrecognised loot names are tried again (ET-460): after the SDE or the price cache has been
/// replaced, and on the log's button. Recognised names move into their runs first, then every saved activity that still
/// had unpriced loot is added up again at the current prices, so a name that is now known and a price that is now there
/// both reach the totals.</summary>
public sealed class UnrecognisedLootRepricer(IServiceScopeFactory scopes) : ISingletonService
{
    /// <summary>How many lines became loot entries.</summary>
    public async Task<int> RepriceAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        Result<int> moved = await dispatcher.Send(new RepriceUnrecognisedLootCommand(), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(OnlyWithUnpricedLoot: true), cancellationToken);
        return moved.IsSuccess ? moved.Value : 0;
    }
}
