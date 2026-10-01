using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.GameLogs;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-417: the view models that build location text mark exactly the location in it — so OPSEC masks the
/// place and leaves the rest of the line (a ship, a count, a time) readable. One test per source the audit found that
/// no screen test already pins down.</summary>
public sealed class OpsecScreenMarkingTests
{
    [Fact]
    public void KillmailRow_MarksSystemRegionAndSecurity()
    {
        KillmailRowViewModel row = _Killmail(isAbyssal: false);

        Assert.Equal($"{OpsecText.Mark("Jita")} · {OpsecText.Mark("The Forge")}", row.SystemLineText);
        Assert.Equal(OpsecText.Mark("0.9"), row.SecurityText);
        Assert.Equal("Rifter", row.ShipText);
    }

    [Fact]
    public void KillmailRow_InAbyssalSpace_MarksThePocketId()
    {
        KillmailRowViewModel row = _Killmail(isAbyssal: true);

        Assert.Equal($"Abyssal deadspace · {OpsecText.Mark("32000001")}", row.SystemLineText);
    }

    [Fact]
    public void GameLogRow_TravelAndNotifyLines_AreMarkedWhole_FightsMiningAndBountiesAreNot()
    {
        var face = new CharacterFaceViewModel(95000001, "Alpha Pilot");

        GameLogRowViewModel travel = new(new("Alpha Pilot", DateTime.UtcNow, GameLogLineKind.Travel, "Jumping from Jita to Perimeter"), face);
        GameLogRowViewModel notify = new(new("Alpha Pilot", DateTime.UtcNow, GameLogLineKind.Notify, "Requested to dock at Jita IV"), face);
        GameLogRowViewModel combat = new(new("Alpha Pilot", DateTime.UtcNow, GameLogLineKind.Combat, "100 to Serpentis Frigate - Hits"), face);

        Assert.Equal(OpsecText.Mark("Jumping from Jita to Perimeter"), travel.Text);
        Assert.Equal(OpsecText.Mark("Requested to dock at Jita IV"), notify.Text);
        Assert.Equal("100 to Serpentis Frigate - Hits", combat.Text);
    }

    [Fact]
    public void CommanderPresence_Tooltip_MarksTheCommandersSystem()
    {
        FleetCommanderPresence presence = FleetCommanderPresence.From("Jita", FleetStandings.At("Jita", "Jita", "Perimeter"));

        Assert.Contains($"are in {OpsecText.Mark("Jita")} with the fleet", presence.Tooltip);
    }

    [AvaloniaFact]
    public void DpsLocation_MarksTheSystem()
    {
        var tracker = new DpsViewModel("Alpha Pilot", isSelf: true) { Location = "Irnin" };

        Assert.Equal(OpsecText.Mark("Irnin"), tracker.LocationDisplay);
    }

    private static KillmailRowViewModel _Killmail(bool isAbyssal) => new(
        new KillmailOverviewRowDto(95000001, 1, new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc), 32000001, IsLoss: false,
            587, null, null, null, 1, null, null, KillmailLinkSource.None, 0, null),
        [new CharacterFaceViewModel(95000001, "Alpha Pilot")], "Rifter", "Jita", "The Forge", isAbyssal, "0.9", "Bravo Pilot",
        TimeZoneInfo.Utc, _ => Task.CompletedTask);
}
