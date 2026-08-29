using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-41: EVE groups the thousands in the client's own language, so one and the same payout reaches the log as
/// "67,500 ISK" or "67.500 ISK". Rejecting either form silently zeroes that pilot's whole bounty column — in the
/// operator's own logs 1279 of 1286 bounty lines used the dot and were dropped.
/// </summary>
public class BountyLineParsingTests
{
    [Theory]
    [InlineData("67,500", 67_500)]      // en
    [InlineData("67.500", 67_500)]      // de/nl — the client that reported ET-41
    [InlineData("4875", 4_875)]         // ungrouped
    [InlineData("1.234.567", 1_234_567)]
    [InlineData("1,234,567", 1_234_567)]
    [InlineData("1.234,56", 1_234)]     // a decimal fraction is dropped — bounty totals are whole ISK
    public void Bounty_IsParsed_WhicheverSeparatorTheClientUses(string amount, long expected)
    {
        var line = "[ 2026.08.28 21:36:56 ] (bounty) <font size=12><b><color=0xff00aa00>"
                   + amount + " ISK</b><color=0x77ffffff> added to next bounty payout";

        var parsed = Assert.IsType<BountyEvent>(LogLineParser.Parse(line));
        Assert.Equal(expected, parsed.Isk);
    }
}
