using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the shared-fit content-hash dedup (the duplicate-on-reshare bug), runnable
/// via <c>--fit-dedup-test</c>. Drives the real <see cref="ISharedFitRepository"/>: re-sharing the same fit — a
/// different ESI id, owner and item order — must match the existing row (returning it, not adding a second), while a
/// genuinely different fit is added. Exit 0 = all passed.
/// </summary>
public static class FittingDedupCheck
{
    private const string Marker = "dedup-check-pilot"; // tags the rows this test creates, for cleanup

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils shared-fit content-hash dedup check ==");
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedFitRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        try
        {
            // First share: a Rifter (587) with a high, a low and some cargo.
            var added = await repository.AddOrMatchAsync(Fit(101, "Rifter PvP", 587,
                Item(2488, "HiSlot0", 1), Item(2048, "LoSlot0", 1), Item(34, "Cargo", 200)), ct);
            ok &= Check("first share is added (no match)", added is null);

            // Re-share the SAME fit: different ESI id, different owner-share id, and the items in a different order.
            var match = await repository.AddOrMatchAsync(Fit(999, "Rifter (copy)", 587,
                Item(34, "Cargo", 200), Item(2488, "HiSlot0", 1), Item(2048, "LoSlot0", 1)), ct);
            ok &= Check("re-share of the same fit is matched (not added)", match is not null);
            ok &= Check("the match names the existing fit it duplicates", match?.Name == "Rifter PvP");

            // A genuinely different fit (one module swapped) is added.
            var other = await repository.AddOrMatchAsync(Fit(102, "Rifter alt", 587,
                Item(2489, "HiSlot0", 1), Item(2048, "LoSlot0", 1), Item(34, "Cargo", 200)), ct);
            ok &= Check("a different fit IS added", other is null);

            var mine = (await repository.ListAsync(ct)).Where(f => f.SharedByCharacterName == Marker).ToList();
            ok &= Check("exactly two rows stored for two distinct fits (the duplicate did not add a third)", mine.Count == 2);
        }
        finally
        {
            foreach (var fit in (await repository.ListAsync(ct)).Where(f => f.SharedByCharacterName == Marker))
                await repository.RemoveAsync(fit.Id, ct);
        }

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static SharedFit Fit(int esiId, string name, int shipTypeId, params (int TypeId, string Flag, int Qty)[] items)
    {
        var itemsJson = string.Join(",", items.Select(i =>
            $$"""{"type_id":{{i.TypeId}},"flag":"{{i.Flag}}","quantity":{{i.Qty}}}"""));
        return new SharedFit
        {
            EsiFittingId = esiId,
            Name = name,
            ShipTypeId = shipTypeId,
            RawJson = $$"""{"fitting_id":{{esiId}},"name":"{{name}}","description":"","ship_type_id":{{shipTypeId}},"items":[{{itemsJson}}]}""",
            SharedByCharacterName = Marker,
            SharedByCharacterId = 999000,
            SharedAt = DateTimeOffset.UtcNow
        };
    }

    private static (int, string, int) Item(int typeId, string flag, int qty) => (typeId, flag, qty);

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
