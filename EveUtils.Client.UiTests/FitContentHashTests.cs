using EveUtils.Shared.Modules.Fittings;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Verifies the order-independent fit content fingerprint (2026-06-04: hull + qty-expanded item type ids,
/// ASC sort, MD5). The same fit must hash identically regardless of item order or quantity grouping; a different
/// ship or module set must hash differently; name/description are ignored.
/// </summary>
public class FitContentHashTests
{
    private static string Fit(int ship, string items, string name = "A", string desc = "") =>
        $$"""{"fitting_id":1,"name":"{{name}}","description":"{{desc}}","ship_type_id":{{ship}},"items":[{{items}}]}""";

    private static string Item(int typeId, string flag, int qty) =>
        $$"""{"type_id":{{typeId}},"flag":"{{flag}}","quantity":{{qty}}}""";

    [Fact]
    public void SameFit_DifferentItemOrder_HashesEqual()
    {
        var a = Fit(587, $"{Item(2048, "LoSlot0", 1)},{Item(34, "Cargo", 100)},{Item(2488, "HiSlot0", 1)}");
        var b = Fit(587, $"{Item(2488, "HiSlot0", 1)},{Item(34, "Cargo", 100)},{Item(2048, "LoSlot0", 1)}");

        Assert.Equal(FitContentHash.Compute(a), FitContentHash.Compute(b));
    }

    [Fact]
    public void SameFit_QuantityGroupingDiffers_HashesEqual()
    {
        var grouped = Fit(587, Item(34, "Cargo", 3));
        var split = Fit(587, $"{Item(34, "Cargo", 1)},{Item(34, "Cargo", 2)}");

        Assert.Equal(FitContentHash.Compute(grouped), FitContentHash.Compute(split));
    }

    [Fact]
    public void Name_And_Description_AreIgnored()
    {
        var a = Fit(587, Item(2488, "HiSlot0", 1), name: "Cheap Rifter", desc: "pvp");
        var b = Fit(587, Item(2488, "HiSlot0", 1), name: "Expensive Rifter", desc: "pve");

        Assert.Equal(FitContentHash.Compute(a), FitContentHash.Compute(b));
    }

    [Fact]
    public void DifferentModules_HashDiffers()
    {
        var a = Fit(587, Item(2488, "HiSlot0", 1));
        var b = Fit(587, Item(2489, "HiSlot0", 1));

        Assert.NotEqual(FitContentHash.Compute(a), FitContentHash.Compute(b));
    }

    [Fact]
    public void SameModules_DifferentShip_HashDiffers()
    {
        var rifter = Fit(587, Item(2488, "HiSlot0", 1));
        var punisher = Fit(597, Item(2488, "HiSlot0", 1));

        Assert.NotEqual(FitContentHash.Compute(rifter), FitContentHash.Compute(punisher));
    }
}
