using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-105 AC-1: the fleet commander's discard ends the shared activity and takes nothing from anybody. There is no
/// group entity to delete (ET-104) — a discard unlinks, and a member who already saved keeps their run as a
/// standalone one, with their own figures still on it.
/// </summary>
public sealed class HomefrontDiscardTests
{
    private const string GroupCode = "HF-A1B2";
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The counter-proof the ticket asks for: member B saves, the FC then discards, and B's row is still there with
    /// B's own data. Both runs sit in one database here, which is stricter than reality — on real machines the
    /// discard could not reach B's row at all, so if anything of B's disappears it is this code that did it.
    /// </summary>
    [AvaloniaFact]
    public async Task FleetCommanderDiscard_AfterAMemberSaved_LeavesThatMembersRunAndItsData()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid commanderRun = await _StartAsync(dispatcher, characterId: 90000001, RunRole.FleetCommander, cancellationToken);
        Guid memberRun = await _StartAsync(dispatcher, characterId: 90000002, RunRole.Member, cancellationToken);

        // Member B commits their own part of the run before the FC throws the run away.
        Result saved = await dispatcher.Send(new SaveRunCommand(memberRun, StartedAtUtc.AddMinutes(14),
            StartedAtUtc.AddMinutes(15),
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                    Source = LootCaptureSource.Clipboard,
                    Entries =
                    [
                        new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 7, Volume = 0.07m, ClipboardPrice = 700m, LootKind = LootKind.Gained }
                    ]
                }
            ],
            [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(6), Isk = 250m }],
            [],
            []), cancellationToken);
        Assert.True(saved.IsSuccess);

        Result<int> discarded = await dispatcher.Send(
            new DiscardRunsInGroupCommand(GroupCode, StartedAtUtc.AddMinutes(20)), cancellationToken);

        Assert.True(discarded.IsSuccess);
        Assert.Equal(2, discarded.Value);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);

        Run member = await db.Set<Run>().Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .SingleAsync(run => run.Id == memberRun, cancellationToken);

        // Still a run, still B's, still saved — only its membership of the group is gone.
        Assert.Null(member.DeletedAtUtc);
        Assert.Equal(RunState.Saved, member.State);
        Assert.Equal(StartedAtUtc.AddMinutes(15), member.SavedAtUtc);
        Assert.Equal(StartedAtUtc.AddMinutes(14), member.StoppedAtUtc);
        Assert.Equal(90000002, member.CharacterId);
        Assert.Null(member.GroupCode);
        Assert.Equal(GroupCode, member.FormerGroupCode);

        // B's own figures, untouched.
        RunLootCapture capture = Assert.Single(member.LootCaptures);
        RunLootEntry entry = Assert.Single(capture.Entries);
        Assert.Equal(700m, entry.ClipboardPrice);
        Assert.Equal(7, entry.Quantity);
        Assert.Equal(250m, Assert.Single(member.BountyEntries).Isk);

        // And the FC's own run was stopped rather than deleted.
        Run commander = await db.Set<Run>().SingleAsync(run => run.Id == commanderRun, cancellationToken);
        Assert.Null(commander.DeletedAtUtc);
        Assert.Equal(RunState.Stopped, commander.State);
        Assert.Equal(StartedAtUtc.AddMinutes(20), commander.StoppedAtUtc);
        Assert.Null(commander.GroupCode);
        Assert.Equal(GroupCode, commander.FormerGroupCode);
    }

    /// <summary>The audit value is written once. A run pulled out of one group and later out of another still names
    /// the first — otherwise the trace of who flew together is silently rewritten by the next discard.</summary>
    [AvaloniaFact]
    public async Task FormerGroupCode_IsWrittenOnceAndNotOverwrittenByALaterDiscard()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid runId = await _StartAsync(dispatcher, characterId: 90000003, RunRole.Member, cancellationToken);
        await dispatcher.Send(new DiscardRunCommand(runId, StartedAtUtc.AddMinutes(5)), cancellationToken);
        await dispatcher.Send(new LinkRunToGroupCodeCommand(runId, "HF-B3T4"), cancellationToken);
        await dispatcher.Send(new DiscardRunCommand(runId, StartedAtUtc.AddMinutes(9)), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == runId, cancellationToken);

        Assert.Equal(GroupCode, run.FormerGroupCode);
        Assert.Null(run.GroupCode);
    }

    /// <summary>The arbiter re-links runs constantly while a fleet settles on one code (ET-103). That is not a
    /// discard, and must not leave an audit stamp behind claiming it was.</summary>
    [AvaloniaFact]
    public async Task ArbiterRelink_DoesNotStampTheDiscardAuditValue()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid runId = await _StartAsync(dispatcher, characterId: 90000004, RunRole.Member, cancellationToken);
        await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(runId), cancellationToken);
        await dispatcher.Send(new LinkRunToGroupCodeCommand(runId, "HF-W1N2"), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == runId, cancellationToken);

        Assert.Null(run.FormerGroupCode);
        Assert.Equal("HF-W1N2", run.GroupCode);
    }

    /// <summary>A discard reaches only the runs in the group it names. A run someone is flying elsewhere is not the
    /// FC's to stop.</summary>
    [AvaloniaFact]
    public async Task Discard_LeavesRunsOutsideTheGroupAlone()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid inGroup = await _StartAsync(dispatcher, characterId: 90000005, RunRole.FleetCommander, cancellationToken);
        Result<Guid> elsewhere = await dispatcher.Send(new StartRunCommand(90000006, ActivityKind.Site,
            StartedAtUtc, 1234, "Other site", 30000142, GroupCode: "HF-0TH3"), cancellationToken);

        await dispatcher.Send(new DiscardRunsInGroupCommand(GroupCode, StartedAtUtc.AddMinutes(3)), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run untouched = await db.Set<Run>().SingleAsync(run => run.Id == elsewhere.Value, cancellationToken);
        Run stopped = await db.Set<Run>().SingleAsync(run => run.Id == inGroup, cancellationToken);

        Assert.Equal(RunState.Running, untouched.State);
        Assert.Equal("HF-0TH3", untouched.GroupCode);
        Assert.Null(untouched.FormerGroupCode);
        Assert.Equal(RunState.Stopped, stopped.State);
    }

    private static async Task<Guid> _StartAsync(IDispatcher dispatcher, long characterId, RunRole role,
        CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site,
            StartedAtUtc, 1234, "Homefront", 30000142, GroupCode: GroupCode, Role: role), cancellationToken);
        Assert.True(started.IsSuccess);
        return started.Value;
    }
}
