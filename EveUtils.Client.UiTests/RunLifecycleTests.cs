using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// How long a run lives, and who may end it.
///
/// The operator reported the same thing ten times: he closed a run window, opened a new one, and got the old run back
/// — its start time, its site, and its commander's group code, which left him reading "Only Jithran … can start, stop
/// or discard this run" on a run he had just started himself. Five tickets (ET-135, ET-147, ET-150, ET-152 and #176)
/// had by then moved where the window reads its fleet id, its group code and its commander from. None of them touched
/// how long the ROW lives, and that was the whole of it: <c>StopRun</c> was a property on the view model and nothing
/// else, so the row stayed Running for the rest of the session and every window that opened afterwards adopted it.
///
/// It is also why a commander's STOP never arrived. Measured 2026-09-03: the sender publishes, the wire carries and
/// the subscription fires — but a window sitting on yesterday's run still holds yesterday's group code, so the code
/// the commander announces does not match and the clock runs on.
/// </summary>
public sealed class RunLifecycleTests
{
    private const long FleetId = 4242;
    private const string GroupCode = "HF-F0CU";
    private const int Commander = 100;
    private const int Member = 200;

    // ── The root ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The counter-proof for ten reports: a run whose clock was stopped is not what the next window comes up on.
    /// The three rows are the three ways a clock comes to rest and the one way it does not — a resumed run is still
    /// this pilot's open run and must still be adopted, or STOP would stop being a pause.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("stopped-by-button")]
    [InlineData("stopped-by-commander")]
    [InlineData("resumed")]
    public async Task AStoppedRun_IsNotWhatTheNextWindowAdopts(string row)
    {
        using var instance = _Instance(out var dialogs, out _);
        await _SeedAsync(instance);
        var bus = instance.Services.GetRequiredService<IEventBus>();
        DateTime startedAt = new(2026, 9, 3, 11, 36, 59, DateTimeKind.Utc);

        var joined = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        joined.JoinFleetRun(_Start(startedAt));
        await _Settle(() => joined.RunId is not null);
        Assert.Equal(ActivityRunState.Running, joined.RunState);

        if (row == "stopped-by-commander")
        {
            await bus.PublishAsync(new FleetRunStoppedEvent(
                new RunGroupStop(FleetId, ActivityKind.Site, GroupCode, DateTime.UtcNow)));
            await _Settle(() => joined.RunState == ActivityRunState.Stopped);
        }
        else
        {
            joined.StopRun(DateTime.UtcNow);
        }

        await _Settle(async () => await _StoredIsRunningAsync(instance) == false);
        Assert.False(await _StoredIsRunningAsync(instance), "STOP left the row Running in the store");

        if (row == "resumed")
        {
            await joined.StartRunCommand.ExecuteAsync(null);
            Assert.Equal(ActivityRunState.Running, joined.RunState);
            Assert.True(await _StoredIsRunningAsync(instance));
        }

        joined.Dispose();   // the window closes; the row is the store's business, not the window's

        using var fresh = new ActivityWindowViewModel(ActivityKind.Site, instance.Services) { SignatureName = "Rogue Drone Infestation" };
        await fresh.LoadAsync();
        await _Settle(() => true);

        if (row == "resumed")
        {
            Assert.Equal(GroupCode, fresh.GroupCode);   // still open, so still this pilot's run to come back to
            return;
        }

        Assert.Null(fresh.GroupCode);
        Assert.Null(fresh.AnchorUtc);
        Assert.Equal(ActivityRunState.NotStarted, fresh.RunState);
        Assert.Equal("Rogue Drone Infestation", fresh.SignatureName);
        // The sentence the operator kept reading on a run he had just started himself.
        Assert.DoesNotContain("commands this fleet", fresh.Authority.StatusText, StringComparison.Ordinal);
    }

    // ── Punt 1: the commander's STOP, over the route it really travels ───────────────────────────────

    /// <summary>
    /// STOP driven the whole way: the commander's window publishes it, the payload is serialised exactly as the
    /// client's outbound envelope does, the real wire registry rebuilds it from that JSON, and only then does a
    /// member's window see it. A fixture that hands the view model a ready-made event proves the subscription and
    /// nothing else — and the subscription was never the broken part.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("matching-code")]
    [InlineData("another-groups-code")]
    public async Task TheCommandersStop_TravelsTheRealWireAndStopsAMembersClock(string row)
    {
        using var commander = _Instance(out var commanderDialogs, out _);
        using var member = _Instance(out _, out _);
        await _SeedAsync(commander);
        await _SeedAsync(member);
        var sent = new List<IIntegrationEvent>();
        commander.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Commander, FleetId, ClientOnly: true, Commander)]);

        using var fc = new ActivityWindowViewModel(ActivityKind.Site, commander.Services);
        await fc.LoadAsync();
        fc.SignatureName = "Suspicious Signal: Secure the Intel";
        fc.SignatureId = "RUS-326";
        using (commander.Services.GetRequiredService<IEventBus>().Subscribe<FleetRunStoppedEvent>(e => sent.Add(e)))
        {
            await fc.StartRunCommand.ExecuteAsync(null);
            Assert.NotNull(fc.GroupCode);

            using var joined = new ActivityWindowViewModel(ActivityKind.Site, member.Services);
            joined.JoinFleetRun(new RunGroupCodeStart(FleetId, ActivityKind.Site,
                row == "matching-code" ? fc.GroupCode! : "HF-ELSE", DateTime.UtcNow.AddMinutes(-4), true,
                "Suspicious Signal: Secure the Intel", "Shousran", "RUS-326"));
            await _Settle(() => joined.RunId is not null);

            fc.StopRunCommand.Execute(null);
            await _Settle(() => sent.Count > 0);
            IIntegrationEvent announced = Assert.Single(sent);

            // Over the wire and back: serialise the way RemoteBusConnectionManager.ToEnvelope does, then let the
            // member's OWN registry rebuild it from that string. A type that is published but not registered dies here.
            string payload = JsonSerializer.Serialize(announced.Data, announced.Data!.GetType());
            IIntegrationEvent? revived = member.Services.GetRequiredService<IEventTypeRegistry>()
                .Deserialize(announced.EventType, payload, Commander);
            Assert.NotNull(revived);

            await member.Services.GetRequiredService<IEventBus>().PublishAsync(revived, EventTarget.Local);
            await _Settle(() => joined.RunState == ActivityRunState.Stopped);

            if (row == "matching-code")
            {
                Assert.Equal(ActivityRunState.Stopped, joined.RunState);
                Assert.NotNull(joined.StoppedAtUtc);
            }
            else
            {
                Assert.Equal(ActivityRunState.Running, joined.RunState);
            }
        }
    }

    // ── Punt 2 (ET-151): the scan id names the same signature for everyone in the system ─────────────

    [AvaloniaTheory]
    [InlineData(null, "RUS-326")]        // the member has none, so the commander's travels
    [InlineData("ABC-123", "ABC-123")]   // the member scanned it himself, and his own stands
    public async Task TheCommandersScanId_TravelsToAMemberWhoHasNoneOfHisOwn(string? memberHas, string expected)
    {
        using var instance = _Instance(out _, out _);
        await _SeedAsync(instance);

        using var joined = new ActivityWindowViewModel(ActivityKind.Site, instance.Services) { SignatureId = memberHas };
        joined.JoinFleetRun(new RunGroupCodeStart(FleetId, ActivityKind.Site, GroupCode, DateTime.UtcNow, true,
            "Suspicious Signal: Secure the Intel", "Shousran", "RUS-326"));
        await _Settle(() => joined.RunId is not null);

        Assert.Equal(expected, joined.SignatureId);
    }

    // ── Punt 3: the caption names the source the figures actually have ───────────────────────────────

    /// <summary>
    /// A caption that names the wrong source is worse than none: it makes a right figure look doubtful and a doubtful
    /// one look right. LOOT, CONSUMED and NET are valued by type id out of the price cache, and the copied ISK column
    /// is never held — so the caption may not go on calling itself the clipboard's.
    /// </summary>
    [AvaloniaFact]
    public void TheLootCaption_NamesThePriceLookupAndNotTheClipboardColumn()
    {
        using var instance = _Instance(out _, out _);
        using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);

        Assert.DoesNotContain("Prices are the clipboard column", window.IskLabel, StringComparison.Ordinal);
        Assert.Contains("price", window.IskLabel, StringComparison.OrdinalIgnoreCase);
        // The one figure that IS the copied column is still named as such, so the line is not a second half-truth.
        Assert.Contains("copied column", window.IskLabel, StringComparison.Ordinal);
    }

    // ── The close question ──────────────────────────────────────────────────────────────────────────

    /// <summary>Closing decides what becomes of the row, because nothing after it will. Cancel is the row that keeps
    /// somebody who hit the close button by accident from losing the run he is still flying.</summary>
    [AvaloniaTheory]
    [InlineData("running-save", true)]
    [InlineData("stopped-discard", true)]
    [InlineData("cancel", false)]
    public async Task ClosingAnUnsavedRun_AsksAndCarriesTheAnswerOut(string row, bool mayClose)
    {
        using var instance = _Instance(out var dialogs, out _);
        await _SeedAsync(instance);
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Commander, FleetId, ClientOnly: true, Commander)]);
        dialogs.OnChoose = (_, _) => row switch
        {
            "running-save" => true,
            "stopped-discard" => false,
            _ => null
        };

        using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await window.LoadAsync();
        window.SignatureName = "Blood Watch";
        await window.StartRunCommand.ExecuteAsync(null);
        if (row != "running-save")
            window.StopRun(DateTime.UtcNow);

        Assert.Equal(mayClose, await window.RequestCloseAsync());
        Assert.Single(dialogs.ChoicePrompts);

        if (row == "cancel")
        {
            Assert.True(await _StoredIsRunningAsync(instance) || window.RunState == ActivityRunState.Stopped);
            return;
        }

        // Either answer ends the run: nothing is left for the next window to pick up.
        Assert.False(await _StoredIsRunningAsync(instance));
        Assert.Equal(row == "running-save" ? ActivityRunState.Saved : ActivityRunState.Stopped, window.RunState);
    }

    /// <summary>
    /// The edge this whole feature can fall through. A joining member is <c>Denied</c> — only the commander ends the
    /// fleet's run — but the row <c>JoinFleetRun</c> made is HIS. Hang the close question on
    /// <c>Authority.CanControl</c> and he cannot answer it, cannot close without keeping exactly the state this
    /// exists to clear, and we have built the same wall with a door painted on it.
    /// </summary>
    [AvaloniaFact]
    public async Task ADeniedMember_CanStillThrowAwayHisOwnRow()
    {
        using var instance = _Instance(out var dialogs, out _);
        await _SeedAsync(instance);
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Member, FleetId, ClientOnly: true, Commander)]);
        dialogs.OnChoose = (_, _) => false;   // discard

        using var joined = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        joined.UseCharacter(Member, "RaymondKrah");
        joined.JoinFleetRun(_Start(DateTime.UtcNow.AddMinutes(-4)));
        await _Settle(() => joined.RunId is not null);
        await joined.RefreshFleetCommandAsync(DateTime.UtcNow);
        Assert.False(joined.Authority.CanControl);

        Assert.True(await joined.RequestCloseAsync());
        Assert.False(await _StoredIsRunningAsync(instance));
    }

    // ── ET-155: the commander's window closes, the member's stays and says why ───────────────────────

    /// <summary>Only the path where something was actually thrown away. A refused command and a cancelled
    /// confirmation both leave the run standing, so the window has to stand with it.</summary>
    [AvaloniaTheory]
    [InlineData("discarded", true)]
    [InlineData("command-failed", false)]
    [InlineData("confirmation-cancelled", false)]
    public async Task DiscardClosesTheCommandersWindow_OnTheSuccessfulPathOnly(string row, bool closes)
    {
        using var instance = _Instance(out var dialogs, out _);
        await _SeedAsync(instance);
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Commander, FleetId, ClientOnly: true, Commander)]);
        dialogs.OnConfirm = (_, _) => Task.FromResult(row != "confirmation-cancelled");

        var closed = 0;
        using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        window.CloseRequested += () => closed++;
        await window.LoadAsync();
        window.SignatureName = "Blood Watch";
        await window.StartRunCommand.ExecuteAsync(null);
        Assert.True(window.Authority.CanControl);

        if (row == "command-failed")
            window.RunId = Guid.NewGuid();   // a row the store does not have: DiscardRunCommand refuses it

        await window.DiscardRunCommand.ExecuteAsync(null);

        Assert.Equal(closes ? 1 : 0, closed);
    }

    /// <summary>
    /// The other side of the same discard. The member did not do this and may only read about it, so his window stays
    /// open with the clock at rest and a line that says what happened — and it does not contradict what the
    /// commander's own confirmation promised him: what he already has is still his.
    /// </summary>
    [AvaloniaFact]
    public async Task AMembersWindow_StopsTheClockAndSaysTheRunWasDiscarded_ButStaysOpen()
    {
        using var instance = _Instance(out _, out _);
        await _SeedAsync(instance);
        var bus = instance.Services.GetRequiredService<IEventBus>();

        var closed = 0;
        using var joined = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        joined.CloseRequested += () => closed++;
        joined.JoinFleetRun(_Start(DateTime.UtcNow.AddMinutes(-4)));
        await _Settle(() => joined.RunId is not null);
        Guid ownRow = joined.RunId!.Value;

        await bus.PublishAsync(new FleetRunDiscardedEvent(
            new RunGroupDiscard(FleetId, ActivityKind.Site, GroupCode, DateTime.UtcNow)));
        await _Settle(() => joined.RunState == ActivityRunState.Discarded);

        Assert.Equal(ActivityRunState.Discarded, joined.RunState);
        Assert.True(joined.HasRunNotice);
        Assert.Contains("discarded", joined.RunNoticeText!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, closed);   // nothing closes a member's window for him — not now, not later

        // The clock really is at rest: two refreshes a minute apart read the same.
        joined.Refresh(DateTime.UtcNow);
        string atRest = joined.ClockText;
        joined.Refresh(DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(atRest, joined.ClockText);

        // "Nobody loses what they already saved" — the row he was given is still his.
        Assert.Equal(ownRow, joined.RunId);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static RunGroupCodeStart _Start(DateTime startedAt) => new(
        FleetId, ActivityKind.Site, GroupCode, startedAt, true,
        "Suspicious Signal: Secure the Intel", "Shousran", "RUS-326");

    private static TestClientInstance _Instance(out RecordingDialogService dialogs, out RecordingToastService toasts)
    {
        var recordedDialogs = new RecordingDialogService();
        var recordedToasts = new RecordingToastService();
        dialogs = recordedDialogs;
        toasts = recordedToasts;
        return TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService>(recordedDialogs);
            services.AddSingleton<IToastService>(recordedToasts);
            services.AddSingleton<ILocalCharacterPresence>(new OnePilotInGame());
        });
    }

    private static async Task _SeedAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Commander));
    }

    private static async Task<bool> _StoredIsRunningAsync(TestClientInstance instance)
    {
        using var scope = instance.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetRunningRunQuery())).IsSuccess;
    }

    private static async Task _Settle(Func<bool> until)
    {
        for (var attempt = 0; attempt < 100 && !until(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task _Settle(Func<Task<bool>> until)
    {
        for (var attempt = 0; attempt < 100 && !await until(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed class OnePilotInGame : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => true;
        public bool? IsInGame(int characterId) => true;
        public IDisposable Subscribe(Action handler) => new Unsubscribed();

        private sealed class Unsubscribed : IDisposable
        {
            public void Dispose() { }
        }
    }
}
