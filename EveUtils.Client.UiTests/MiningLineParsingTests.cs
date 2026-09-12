using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-229: the residue line is its own form, "Additional N units depleted from asteroid as residue", with no ore
/// name at all. The parser used to expect it inline on the "You mined" line ("with a lost residue of N units"), a
/// form that never occurs in a real log file — Jithran's own 2026-06-14 and 2026-08-28/29 gamelogs use only the
/// standalone form below.
/// </summary>
public class MiningLineParsingTests
{
    [Fact]
    public void ResidueLine_IsParsed_AsItsOwnEvent_WithNoOreName()
    {
        var line = "[ 2026.06.14 09:52:40 ] (mining) <color=0x77ffffff>Additional <font size=12>"
                   + "<color=#ffff454b>623<color=0x77ffffff><font size=10> units depleted from asteroid as residue";

        var parsed = Assert.IsType<MiningResidueEvent>(LogLineParser.Parse(line));
        Assert.Equal(623, parsed.Units);
    }

    [Fact]
    public void MinedLine_IsParsed_AsANonCriticalMiningEvent()
    {
        var line = "[ 2026.06.14 13:18:29 ] (mining) <color=0x77ffffff>You mined <font size=12>"
                   + "<color=#ff8dc169>624<color=0x77ffffff><font size=10> units of <color=0xffffffff><font size=12>Raspite X-Grade";

        var parsed = Assert.IsType<MiningEvent>(LogLineParser.Parse(line));
        Assert.Equal(624, parsed.Units);
        Assert.Equal("Raspite X-Grade", parsed.OreType);
        Assert.False(parsed.IsCritical);
        Assert.Equal(0, parsed.LostResidue);
    }

    [Fact]
    public void CriticalMinedLine_IsParsed_AsACriticalMiningEvent()
    {
        var line = "[ 2026.06.14 09:52:40 ] (mining) <color=#fff0ff45>Critical mining success!<color=0x77ffffff>"
                   + "<font size=10> You mined an additional <color=#fff0ff45><font size=12>1797<color=0x77ffffff>"
                   + "<font size=10> units of <color=0xffffffff><font size=12>Raspite X-Grade";

        var parsed = Assert.IsType<MiningEvent>(LogLineParser.Parse(line));
        Assert.Equal(1797, parsed.Units);
        Assert.Equal("Raspite X-Grade", parsed.OreType);
        Assert.True(parsed.IsCritical);
    }

    /// <summary>
    /// The counterproof shape from ET-229: mined units plus residue reconstruct the asteroid's whole depleted
    /// amount, once the residue line is correlated to its ore rather than dropped (the bug this ticket fixes). The
    /// full-scale version of this proof — the 7 real Metaliminal sites from 2026-08-28/29 each summing to exactly
    /// 5,000 — is reported in the PR, not embedded here as hundreds of near-identical log lines.
    /// </summary>
    [Fact]
    public void MinedPlusResidue_ReconstructsTheWholeDepletedAmount()
    {
        string[] lines =
        [
            "[ 2026.08.28 19:55:40 ] (mining) You mined 40 units of Amperum Mutanite",
            "[ 2026.08.28 19:55:54 ] (mining) You mined 13 units of Amperum Mutanite",
            "[ 2026.08.28 19:55:54 ] (mining) Additional 13 units depleted from asteroid as residue",
            "[ 2026.08.28 19:56:10 ] (mining) You mined 14 units of Amperum Mutanite",
            "[ 2026.08.28 19:56:10 ] (mining) Additional 6 units depleted from asteroid as residue",
            "[ 2026.08.28 19:56:25 ] (mining) You mined 14 units of Amperum Mutanite"
        ];

        int minedTotal = 0;
        int residueTotal = 0;
        foreach (string line in lines)
        {
            switch (LogLineParser.Parse(line))
            {
                case MiningEvent { IsCritical: false } mined:
                    minedTotal += mined.Units;
                    break;
                case MiningResidueEvent residue:
                    residueTotal += residue.Units;
                    break;
            }
        }

        Assert.Equal(100, minedTotal + residueTotal);
    }
}
