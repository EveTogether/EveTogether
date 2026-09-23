using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-321: a remote-rep counterparty is "&lt;ship [tickers] fit title&gt; - &lt;module&gt;". The parser used to take
/// everything before the <em>first</em> " - ", which truncates the counterparty whenever the ship name or fit title
/// contains one itself. The fix anchors on the module at the end instead.
/// </summary>
public class RemoteRepLineParsingTests
{
    [Fact]
    public void Counterparty_KeepsTheFullLabel_WhenTheShipNameContainsADash()
    {
        // The ticket's own reproduction (ET-321): a fit title with a dash after the ship name's own dash.
        var line = "[ 2026.09.23 12:00:00 ] (combat) 466 remote shield boosted to RaymondKrah [TEST][ALLY] "
                   + "Fedo - Rifter | HoS - Shield - Small Remote Shield Booster II";

        var parsed = Assert.IsType<RemoteRepEvent>(LogLineParser.Parse(line));
        Assert.Equal("RaymondKrah [TEST][ALLY] Fedo - Rifter | HoS - Shield", parsed.Counterparty);
        Assert.Equal(466, parsed.Amount);
        Assert.Equal("shield", parsed.Kind);
        Assert.True(parsed.Outgoing);
    }

    [Fact]
    public void Counterparty_KeepsTheFullLabel_WhenOnlyTheFitTitleContainsADash()
    {
        // Real line, Jithran's gamelog, 2026-06-06 10:41:12 — fit title "HoS - Shield".
        var line = "[ 2026.06.06 10:41:12 ] (combat) <color=0xffccff66><b>488</b><color=0x77ffffff><font size=10>"
                   + " remote shield boosted by </font><b><color=0xffffffff><font size=12><color=0xFFFFFFFF><b>"
                   + "HotSprockets</b> </color></font><font size=12>[PTOMO]</font> <font size=12><color=0xFFFFFFFF>"
                   + "<b>Osprey</b></color></font><font size=12><color=0xFFFF4040> | <i>HoS - Shield</i></color>"
                   + "</font></b><color=0x77ffffff><font size=10> - Medium Murky Compact Remote Shield Booster</font>";

        var parsed = Assert.IsType<RemoteRepEvent>(LogLineParser.Parse(line));
        Assert.Equal("HotSprockets [PTOMO] Osprey | HoS - Shield", parsed.Counterparty);
        Assert.Equal(488, parsed.Amount);
        Assert.False(parsed.Outgoing);
    }

    [Fact]
    public void Counterparty_IsUnchanged_WhenTheLabelHasNoDash()
    {
        // Real line, Jithran's gamelog, 2026-06-11 19:38:45 — no fit title, so only the module's own " - " applies.
        var line = "[ 2026.06.11 19:38:45 ] (combat) <color=0xffccff66><b>16</b><color=0x77ffffff><font size=10>"
                   + " remote shield boosted by </font><b><color=0xffffffff><font size=12><color=0xFFFFFFFF><b>"
                   + "SoldierJRNL</b> </color></font><font size=12><color=0xFFFFB300>[EWB-C]</color></font>"
                   + "<font size=12>[RIPRC]</font> <font size=12><color=0xFFFFFFFF><b>Osprey</b></color></font>"
                   + "</b><color=0x77ffffff><font size=10> - Medium Murky Compact Remote Shield Booster</font>";

        var parsed = Assert.IsType<RemoteRepEvent>(LogLineParser.Parse(line));
        Assert.Equal("SoldierJRNL [EWB-C][RIPRC] Osprey", parsed.Counterparty);
        Assert.Equal(16, parsed.Amount);
    }
}
