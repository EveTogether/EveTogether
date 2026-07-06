using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Modules.Fleet;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The roster-change toaster: turns the boss-side diff transitions into toasts a user would miss
/// with the roster window closed — planned pilots joining/leaving and unplanned pilots arriving. The first observation
/// (no previous diff) is a silent baseline so it never spams on startup.
/// </summary>
public class FleetRosterChangeNotifierTests
{
    private static FleetRosterChangeNotifier Create(RecordingToastService toasts, FakeExternalLookup names) =>
        new(toasts, names);

    [Fact]
    public async Task FirstObservation_WithNoPrevious_DoesNotToast()
    {
        var toasts = new RecordingToastService();
        var notifier = Create(toasts, new FakeExternalLookup());

        await notifier.NotifyAsync(previous: null, new FleetRosterDiff([100, 200], [], [999]), TestContext.Current.CancellationToken);

        Assert.Empty(toasts.Toasts);
    }

    [Fact]
    public async Task PlannedPilotJoined_ToastsSuccessWithName()
    {
        var toasts = new RecordingToastService();
        var notifier = Create(toasts, new FakeExternalLookup { [200] = "Jithran" });

        await notifier.NotifyAsync(
            new FleetRosterDiff([100], [200], []),
            new FleetRosterDiff([100, 200], [], []),
            TestContext.Current.CancellationToken);

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Jithran joined the fleet", toast.Title);
        Assert.Equal(ToastKind.Success, toast.Kind);
    }

    [Fact]
    public async Task PlannedPilotLeft_ToastsWarning()
    {
        var toasts = new RecordingToastService();
        var notifier = Create(toasts, new FakeExternalLookup { [200] = "Jithran" });

        await notifier.NotifyAsync(
            new FleetRosterDiff([100, 200], [], []),
            new FleetRosterDiff([100], [200], []),
            TestContext.Current.CancellationToken);

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Jithran left the fleet", toast.Title);
        Assert.Equal(ToastKind.Warning, toast.Kind);
    }

    [Fact]
    public async Task UnplannedPilotArrived_ToastsInformation()
    {
        var toasts = new RecordingToastService();
        var notifier = Create(toasts, new FakeExternalLookup { [999] = "Stranger" });

        await notifier.NotifyAsync(
            new FleetRosterDiff([100], [], []),
            new FleetRosterDiff([100], [], [999]),
            TestContext.Current.CancellationToken);

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Stranger joined — not in the plan", toast.Title);
        Assert.Equal(ToastKind.Information, toast.Kind);
    }

    [Fact]
    public async Task ManyJoinedAtOnce_AggregatesToCountWithNamesInMessage()
    {
        var toasts = new RecordingToastService();
        var names = new FakeExternalLookup { [1] = "Alpha", [2] = "Bravo", [3] = "Charlie" };
        var notifier = Create(toasts, names);

        await notifier.NotifyAsync(
            new FleetRosterDiff([], [], []),
            new FleetRosterDiff([1, 2, 3, 4], [], []),
            TestContext.Current.CancellationToken);

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("4 pilots joined the fleet", toast.Title);
        Assert.Equal("Alpha, Bravo, Charlie +1 more", toast.Message);
    }

    [Fact]
    public async Task UnknownName_FallsBackToPilotId()
    {
        var toasts = new RecordingToastService();
        var notifier = Create(toasts, new FakeExternalLookup()); // no names seeded

        await notifier.NotifyAsync(
            new FleetRosterDiff([], [], []),
            new FleetRosterDiff([4242], [], []),
            TestContext.Current.CancellationToken);

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Pilot 4242 joined the fleet", toast.Title);
    }

    [Fact]
    public async Task NoChange_DoesNotToast()
    {
        var toasts = new RecordingToastService();
        var notifier = Create(toasts, new FakeExternalLookup());

        var same = new FleetRosterDiff([100, 200], [], []);
        await notifier.NotifyAsync(same, new FleetRosterDiff([100, 200], [], []), TestContext.Current.CancellationToken);

        Assert.Empty(toasts.Toasts);
    }
}
