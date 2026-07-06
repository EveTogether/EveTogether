using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Verifies content-hash dedup on ESI import (2026-06-04): two ESI fittings that are the same fit (same ship +
/// modules, different fitting id / item order / name) import as ONE local row, and the skip is reported naming the
/// fit it matched. A re-import of the same content imports nothing more.
/// </summary>
public class FittingImportDedupTests
{
    private const int CharId = 95000123;

    private static EsiFitting Fit(int fittingId, string name, int ship, params EsiFittingItem[] items) =>
        new(fittingId, name, "", ship, items);

    private static EsiFittingItem Item(int typeId, string flag, int qty) => new(typeId, flag, qty);

    [Fact]
    public async Task Import_TwoContentIdenticalFits_StoresOne_AndReportsTheMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var repo = instance.Services.GetRequiredService<IFittingRepository>();

        // Same ship + same modules, but a different fitting id, reordered items and a different name → one fit.
        var first = Fit(1, "Rifter PvP", 587, Item(2488, "HiSlot0", 1), Item(2048, "LoSlot0", 1), Item(34, "Cargo", 200));
        var dup = Fit(2, "Rifter (copy)", 587, Item(34, "Cargo", 200), Item(2048, "LoSlot0", 1), Item(2488, "HiSlot0", 1));

        var result = await dispatcher.Send(new ImportFittingsFromEsiCommand(CharId, new List<EsiFitting> { first, dup }), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value); // only one stored
        var skip = Assert.Single(result.Messages, m => m.Code == MessageCodes.Duplicate);
        Assert.Contains("Rifter PvP", skip.Text); // names the fit it matched
        Assert.Single(await repo.ListAllAsync(ct));

        // Re-importing the same content adds nothing.
        var again = await dispatcher.Send(new ImportFittingsFromEsiCommand(CharId, new List<EsiFitting> { dup }), ct);
        Assert.Equal(0, again.Value);
        Assert.Single(await repo.ListAllAsync(ct));
    }
}
