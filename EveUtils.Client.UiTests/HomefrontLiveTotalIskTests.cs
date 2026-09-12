using System;
using System.Linq;
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
/// ET-269 (Jithran, 2026-09-12, HF-68RM): clicking Completed in the run window used to make TOTAL ISK flicker
/// continuously between the full homefront payout and a bounty-only figure. Two readers disagreed about the
/// header's own number — <see cref="ActivityWindowViewModel._RefreshGroupTotalIsk"/> read
/// <see cref="EveUtils.Client.ViewModels.Runs.RunParticipantViewModel"/>'s own <c>HomefrontOutcome</c>, which is only
/// a mirror of the database, refreshed on its own asynchronous schedule (<c>_RefreshParticipantsAsync</c>) — while
/// HOMEFRONT itself already knew the outcome in memory, the moment <c>SetOutcome</c> was clicked. The fix reads the
/// section's own live decision (<see cref="HomefrontWindowSectionViewModel.LiveDecision"/>) instead, so there is only
/// ever the one number, computed the same tick the pick was made — never a race with the database at all.
/// </summary>
public sealed class HomefrontLiveTotalIskTests
{
    private static readonly SdeSite RaidSite = new(10347, "Raid: Hall of Sacrifice", ArchetypeId: 70,
        ArchetypeName: null, FactionId: null, FactionName: null, Description: null, DedRating: null,
        IsShipRestricted: false, AllowedShipGroups: []);

    [AvaloniaFact]
    public async Task ManuallyMarkingCompleted_TheHeaderShowsTheFullPayoutOnTheVeryNextTick_BeforeTheDatabaseEverCatchesUp()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync();
        window.MatchedSites = [RaidSite];
        await window.StartRunCommand.ExecuteAsync(null);
        Guid runId = window.RunId ?? throw new InvalidOperationException("the run did not start");

        DateTime clock = DateTime.UtcNow;
        HomefrontWindowSectionViewModel? section = null;
        for (int tick = 0; tick < 30 && (section is null || !section.CanDecide || section.Rows.Count == 0); tick++)
        {
            clock = clock.AddSeconds(1);
            window.Refresh(clock);
            section = window.Sections.OfType<HomefrontWindowSectionViewModel>().SingleOrDefault();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.NotNull(section);
        Assert.True(section!.CanDecide);
        // Solo and own, so ET-269's default puts the pilot in the site with nothing to tick — N = 1.
        Assert.True(Assert.Single(section.Rows).IsInSite);

        section.SetOutcomeCommand.Execute(HomefrontOutcome.Completed);

        // The very next tick, synchronously — no await, no job pump in between: whatever _RefreshParticipantsAsync's
        // own asynchronous database mirror is doing, it cannot possibly have run yet.
        clock = clock.AddSeconds(1);
        window.Refresh(clock);

        Assert.True(window.HasGroupTotalIsk);
        // Raid's own N=1 table figure (domain/homefronts.md §3.2) — never "expected"/"part expected": ET-269 counts
        // it the moment the site reads Completed, there being no wallet to wait a confirmation on.
        Assert.Contains("6,144,000", window.GroupTotalIskText);
        Assert.DoesNotContain("expected", window.GroupTotalIskText, StringComparison.OrdinalIgnoreCase);

        // And it never flickers back once the bundled attendance write lands and the database mirror catches up.
        for (int tick = 0; tick < 10; tick++)
        {
            clock = clock.AddSeconds(1);
            window.Refresh(clock);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
            Assert.Contains("6,144,000", window.GroupTotalIskText);
        }

        Run written = await _RunAsync(harness, runId);
        Assert.Equal(HomefrontOutcome.Completed, written.HomefrontOutcome);
    }

    private static async Task<Run> _RunAsync(ActivityWindowHarness harness, Guid runId)
    {
        await using ClientDbContext db = await harness.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<Run>().AsNoTracking().SingleAsync(candidate => candidate.Id == runId);
    }
}
