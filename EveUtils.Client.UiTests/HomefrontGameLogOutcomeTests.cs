using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-262: at a Metaliminal Meteoroid homefront, the "pale shadow" gamelog line — the notify EVE writes once the
/// site's single asteroid runs dry — sets the run's own outcome to completed by itself, and says so ("completed ·
/// from the game log"). Still editable by hand afterwards, and never fired for any other homefront kind.
///
/// The line is copied verbatim from Jithran's own gamelogs (<c>Documents\EVE\logs\Gamelogs\20260829_075933_90250177.txt:641</c>,
/// read-only, 2026-08-29): only the leading mining module varies ("Miner II", "Mining Drone II", "Modulated Strip
/// Miner II", …), never the fixed tail this ticket matches on.
/// </summary>
public sealed class HomefrontGameLogOutcomeTests
{
    // The dungeon id alone resolves TYPE (RunTypeCatalogue.For, ET-228) — same rule FleetRunMiningSharingTests uses.
    private static readonly SdeSite MetaliminalSite = new(10312, "Metaliminal Meteoroid: Amarr Mining", ArchetypeId: 70,
        ArchetypeName: null, FactionId: null, FactionName: null, Description: null, DedRating: null,
        IsShipRestricted: false, AllowedShipGroups: []);

    private static readonly SdeSite RaidSite = new(10347, "Raid: Hall of Sacrifice", ArchetypeId: 70,
        ArchetypeName: null, FactionId: null, FactionName: null, Description: null, DedRating: null,
        IsShipRestricted: false, AllowedShipGroups: []);

    private static string _PaleShadowLine(string module = "Miner II") =>
        $"[ 2030.01.01 12:00:10 ] (notify) {module} deactivates as it finds the resource it was harvesting a pale shadow of its former glory.";

    [AvaloniaFact]
    public async Task PaleShadowLine_OnAMetaliminalHomefront_SetsOutcomeCompletedFromTheGameLog()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync();
        window.MatchedSites = [MetaliminalSite];
        await window.StartRunCommand.ExecuteAsync(null);
        Guid runId = window.RunId ?? throw new InvalidOperationException("the run did not start");
        using HomefrontWindowSectionViewModel section = new(window);
        await harness.StartWatchingAsync();

        await harness.WriteLineAsync(_PaleShadowLine());
        Run run = await _TickUntilAsync(harness, window, section, runId, run => run.HomefrontOutcome is not null);

        Assert.Equal(HomefrontOutcome.Completed, run.HomefrontOutcome);
        Assert.True(run.HomefrontOutcomeFromGameLog);
        Assert.Equal("completed · from the game log", section.OutcomeText);
    }

    /// <summary>Counter-proof: the same line, a homefront kind it says nothing about. AC-2.</summary>
    [AvaloniaFact]
    public async Task PaleShadowLine_OnARaid_NeverSetsAnOutcome()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync();
        window.MatchedSites = [RaidSite];
        await window.StartRunCommand.ExecuteAsync(null);
        Guid runId = window.RunId ?? throw new InvalidOperationException("the run did not start");
        using HomefrontWindowSectionViewModel section = new(window);
        await harness.StartWatchingAsync();

        await harness.WriteLineAsync(_PaleShadowLine());
        Run run = await _TickUntilAsync(harness, window, section, runId, _ => true, ticks: 15);

        Assert.Null(run.HomefrontOutcome);
        Assert.False(run.HomefrontOutcomeFromGameLog);
        Assert.Equal("not decided", section.OutcomeText);
    }

    /// <summary>The line only ever fills in an undecided outcome — a hand pick, "failed" included, stands over it.</summary>
    [AvaloniaFact]
    public async Task AManuallyChosenOutcome_IsNeverOverriddenByALaterPaleShadowLine()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync();
        window.MatchedSites = [MetaliminalSite];
        await window.StartRunCommand.ExecuteAsync(null);
        Guid runId = window.RunId ?? throw new InvalidOperationException("the run did not start");
        using HomefrontWindowSectionViewModel section = new(window);
        await harness.StartWatchingAsync();

        // CanDecide only settles once the section has ticked at least once (Role.Pilot).
        window.Refresh(DateTime.UtcNow);
        section.Refresh(DateTime.UtcNow);
        Assert.True(section.CanDecide);
        section.SetOutcomeCommand.Execute(HomefrontOutcome.Failed);

        await harness.WriteLineAsync(_PaleShadowLine());
        Run run = await _TickUntilAsync(harness, window, section, runId, run => run.HomefrontOutcome is not null, ticks: 30);

        Assert.Equal(HomefrontOutcome.Failed, run.HomefrontOutcome);
        Assert.False(run.HomefrontOutcomeFromGameLog);
        Assert.Equal("failed", section.OutcomeText);
    }

    private static async Task<Run> _TickUntilAsync(ActivityWindowHarness harness, ActivityWindowViewModel window,
        HomefrontWindowSectionViewModel section, Guid runId, Func<Run, bool> until, int ticks = 60)
    {
        DateTime clock = DateTime.UtcNow;
        Run run = await _RunAsync(harness, runId);
        for (int tick = 0; tick < ticks && !until(run); tick++)
        {
            clock = clock.AddSeconds(1);
            window.Refresh(clock);
            section.Refresh(clock);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
            run = await _RunAsync(harness, runId);
        }

        return run;
    }

    private static async Task<Run> _RunAsync(ActivityWindowHarness harness, Guid runId)
    {
        await using ClientDbContext db = await harness.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<Run>().AsNoTracking().SingleAsync(candidate => candidate.Id == runId);
    }
}
