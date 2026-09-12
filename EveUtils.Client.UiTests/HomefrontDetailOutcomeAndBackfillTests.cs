using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Attendance;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-269, Jithran's own two measured groups. HF-KQWB: five own runs, never marked Completed, and no way on the
/// saved detail screen to fix that. HF-V7MB: one own run only, but all five own characters ticked in the site — the
/// other four own toons' own payout and loot never reached TOTAL ISK because they never had a run of their own.
/// </summary>
public sealed class HomefrontDetailOutcomeAndBackfillTests
{
    private const int Jithran = 90000401;
    private const int Abnoba = 90000402;
    private const int HotSprockets = 90000403;
    private const int ColdSprockets = 90000404;
    private const int Noahmarr = 90000405;
    private const int HallOfSacrifice = 10347;
    private static readonly DateTime StartedAtUtc = new(2026, 9, 12, 20, 27, 0, DateTimeKind.Utc);

    /// <summary>HF-KQWB, after ET-269: a saved activity with five own runs and an undecided outcome can still be
    /// marked Completed from the detail screen, the payout counts at once (no "expected", ET-269 overriding ET-231),
    /// and correcting who was in site afterwards never wipes the outcome back to "not decided" (the bug
    /// <see cref="HomefrontDetailSectionViewModel.SaveEditAsync"/> had: it built a brand new decision with only the
    /// list, defaulting Outcome to null).</summary>
    [AvaloniaFact]
    public async Task ASavedActivityWithFiveOwnRuns_CanBeMarkedCompletedAfterwards_AndAnAttendanceCorrectionKeepsIt()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        int[] own = [Jithran, Abnoba, HotSprockets, ColdSprockets, Noahmarr];
        Guid[] runs = await Task.WhenAll(own.Select(id => _StartAsync(dispatcher, id, "HF-KQWB")));

        // The window's own attendance decision, undecided outcome — exactly HF-KQWB's own measured state.
        RunAttendanceDecision decision = new(
            [.. own.Select(id => new RunAttendanceEntryInput { CharacterId = id, IsInSite = true, Reason = AttendanceReason.NoActivityLogged })],
            0, AttendanceSource.Pilot, Jithran, StartedAtUtc.AddMinutes(9));
        await dispatcher.Send(new SetRunAttendanceCommand(decision, own.Select(id => (long)id).ToArray(), "HF-KQWB"));
        foreach (Guid run in runs)
            await dispatcher.Send(new SaveRunCommand(run, StartedAtUtc.AddMinutes(9), StartedAtUtc.AddMinutes(10), [], [], [], []));
        await dispatcher.Send(new RebuildActivitySummariesCommand());

        (HomefrontDetailSectionViewModel section, _) = await _DetailAsync(instance, "HF-KQWB", own);
        Assert.Equal("not decided", section.OutcomeText);
        Assert.True(section.CanDecideOutcome);

        await section.SetOutcomeCommand.ExecuteAsync(HomefrontOutcome.Completed);
        await dispatcher.Send(new RebuildActivitySummariesCommand());

        (section, _) = await _DetailAsync(instance, "HF-KQWB", own);
        Assert.Equal("completed", section.OutcomeText);
        Assert.Contains("75,000,000 ISK", section.PayoutSummaryText); // 5 × 15,000,000 — Raid's own N=5 figure.
        AttendanceRowViewModel jithranRow = section.Rows.Single(row => row.CharacterId == Jithran);
        Assert.Equal("15,000,000 ISK", jithranRow.PayoutText); // never "expected"/"confirmed" (ET-269).

        IskBreakdown isk = (await _RowAsync(instance, "HF-KQWB")).Isk;
        Assert.Equal(IskCertainty.Measured, isk.Of(IskSource.HomefrontPayout)?.Certainty);
        Assert.Equal(75_000_000m, isk.Of(IskSource.HomefrontPayout)?.Amount);

        // Now correct who was in the site — Noahmarr out — without touching the outcome at all.
        section.EditCommand.Execute(null);
        section.Rows.Single(row => row.CharacterId == Noahmarr).IsInSite = false;
        await section.SaveEditCommand.ExecuteAsync(null);
        await dispatcher.Send(new RebuildActivitySummariesCommand());

        (section, _) = await _DetailAsync(instance, "HF-KQWB", own);
        Assert.Equal("completed", section.OutcomeText); // ET-269: the outcome survived the correction.
        Assert.False(section.Rows.Single(row => row.CharacterId == Noahmarr).IsInSite);
        Assert.Contains("48,000,000 ISK", section.PayoutSummaryText); // 4 × 12,000,000 — Raid's own N=4 figure.
    }

    /// <summary>HF-V7MB: only Jithran has a run, but all five own characters are ticked in the site once attendance
    /// is decided. Each own character ticked in gets a run of its own in the group, cloned from the group's own site
    /// and times — the one thing that lets its own payout and loot ever reach TOTAL ISK (ET-269).</summary>
    [AvaloniaFact]
    public async Task AnOwnCharacterTickedInWithNoRunOfItsOwn_GetsOneBackfilled()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid jithranRun = await _StartAsync(dispatcher, Jithran, "HF-V7MB");
        int[] own = [Jithran, Abnoba, HotSprockets, ColdSprockets, Noahmarr];

        RunAttendanceDecision decision = new(
            [.. own.Select(id => new RunAttendanceEntryInput { CharacterId = id, IsInSite = true, Reason = AttendanceReason.NoActivityLogged })],
            0, AttendanceSource.Pilot, Jithran, StartedAtUtc.AddMinutes(9), HomefrontOutcome.Completed);
        Result<int> written = await dispatcher.Send(
            new SetRunAttendanceCommand(decision, own.Select(id => (long)id).ToArray(), "HF-V7MB"));
        Assert.Equal(5, written.Value); // Jithran's own row, plus four backfilled.

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        List<Run> groupRuns = await db.Set<Run>().Where(run => run.GroupCode == "HF-V7MB").ToListAsync();
        Assert.Equal(5, groupRuns.Count);
        Assert.Equal(own.Order(), groupRuns.Select(run => (int)run.CharacterId).Order());
        Run abnobaRun = groupRuns.Single(run => run.CharacterId == Abnoba);
        Assert.Equal(HallOfSacrifice, abnobaRun.SiteTypeId);
        Assert.Equal(SiteTypeSource.Site, abnobaRun.SiteTypeSource);
        Assert.Equal("Raid: Hall of Sacrifice", abnobaRun.SiteName);
        Assert.Equal(RunState.Running, abnobaRun.State); // cloned from the template's own (still-running) state.
        Assert.True(abnobaRun.IsParticipant);
        Assert.Equal(5, abnobaRun.AttendanceCount);
        Assert.Equal(HomefrontOutcome.Completed, abnobaRun.HomefrontOutcome);
        Assert.NotEqual(jithranRun, abnobaRun.Id);

        // A second, identical decision creates nothing more — the backfill is idempotent.
        Result<int> resent = await dispatcher.Send(
            new SetRunAttendanceCommand(decision, own.Select(id => (long)id).ToArray(), "HF-V7MB"));
        Assert.Equal(0, resent.Value);
        Assert.Equal(5, await db.Set<Run>().CountAsync(run => run.GroupCode == "HF-V7MB"));
    }

    private static async Task<Guid> _StartAsync(IDispatcher dispatcher, long characterId, string groupCode)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            HallOfSacrifice, "Raid: Hall of Sacrifice", 30000142, groupCode));
        Assert.True(started.IsSuccess);
        return started.Value;
    }

    private static async Task<ActivityOverviewRowDto> _RowAsync(TestClientInstance instance, string groupCode)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        IReadOnlyList<ActivityOverviewRowDto> overview = (await dispatcher.Query(new GetActivityOverviewQuery())).Value!;
        return overview.Single(candidate => candidate.GroupCode == groupCode);
    }

    private static async Task<(HomefrontDetailSectionViewModel Section, ActivityDetailDto Detail)> _DetailAsync(
        TestClientInstance instance, string groupCode, int[] own)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        ActivityOverviewRowDto row = await _RowAsync(instance, groupCode);
        ActivityDetailDto detail = (await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId))).Value!;
        RunTypeDefinition type = RunTypeCatalogue.For(detail.ActivityKind, detail.SignatureGroupSnapshot, detail.SiteTypeId);
        RunDetailSectionInput input = new(detail, type, id => $"Char {id}");

        RunDetailSectionServices services = new(dispatcher, null, null, null, null, null, null, null,
            new HashSet<long>(own.Select(id => (long)id)), instance.Services);
        HomefrontDetailSectionViewModel section = new(services);
        section.Apply(input);
        await section.LoadAsync(input, followUp: false, CancellationToken.None);
        return (section, detail);
    }
}
