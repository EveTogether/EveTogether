using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.Views;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-165. A fleet only counts as participation once it has actually been started, and that has to hold for both
/// halves of <see cref="FleetParticipationRefresher"/> — a fleet living in this client's own database rather than
/// on a server says where it is kept, not whether the FC has started it.
///
/// The two halves had drifted: the server half asked for <see cref="FleetActivation.Active"/>, the client-only half
/// let through anything that was merely not <see cref="FleetActivation.Concluded"/>. Two locally prepared fleets
/// were then enough to hand <c>ActivityWindowViewModel._ActingFleetId</c> a set of two, which it answers with null —
/// so a run lost its group code and went silently solo without one fleet having been started.
/// </summary>
public class FleetParticipationActivationTests
{
    private const string Server = "srv:7443";
    private const int Owner = 95000001;
    private const long ServerFleetId = 11;

    /// <summary>
    /// The rule itself, asked of both halves at once so they cannot drift apart again: one server fleet and one
    /// client-only fleet at the same activation, one sweep, and both are expected to answer the same.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetActivation.Forming, false)]
    [InlineData(FleetActivation.Active, true)]
    [InlineData(FleetActivation.Concluded, false)]
    public async Task ServerAndClientOnlyFleets_AnswerTheSameActivationRule(
        FleetActivation activation, bool participates)
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [_ServerFleet(activation)];

        using var instance = TestClientInstance.Create(
            services => services.AddSingleton<IFleetTransportClient>(transport));

        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Jithran", Owner));
        long localFleetId = await _CreateLocalFleetAsync(instance, activation);

        await instance.Services.GetRequiredService<FleetParticipationRefresher>().RefreshAsync();

        var fleetIds = instance.Services.GetRequiredService<IFleetParticipation>()
            .Current.Select(participant => participant.FleetId).ToHashSet();
        Assert.Equal(participates, fleetIds.Contains(ServerFleetId));
        Assert.Equal(participates, fleetIds.Contains(localFleetId));
    }

    /// <summary>
    /// ET-165's own report: two local fleets set up for later and neither started. Nothing has been broadcast, so
    /// nothing participates — and the run window is left with an empty set rather than an ambiguous one, which is
    /// the difference between a solo run and a run that quietly lost its fleet.
    /// </summary>
    [AvaloniaFact]
    public async Task TwoPreparedLocalFleets_NeitherStarted_ContributeNothing()
    {
        using var instance = TestClientInstance.Create();
        await _CreateLocalFleetAsync(instance, FleetActivation.Forming);
        await _CreateLocalFleetAsync(instance, FleetActivation.Forming);

        await instance.Services.GetRequiredService<FleetParticipationRefresher>().RefreshAsync();

        Assert.Empty(instance.Services.GetRequiredService<IFleetParticipation>().Current);
    }

    /// <summary>Start one of the two and the answer is no longer a question: the run hangs on the fleet that was
    /// started, and the one still standing by does not muddy it.</summary>
    [AvaloniaFact]
    public async Task OneStartedLocalFleet_BesideAPreparedOne_IsTheOnlyParticipation()
    {
        using var instance = TestClientInstance.Create();
        long started = await _CreateLocalFleetAsync(instance, FleetActivation.Active);
        await _CreateLocalFleetAsync(instance, FleetActivation.Forming);

        await instance.Services.GetRequiredService<FleetParticipationRefresher>().RefreshAsync();

        var participant = Assert.Single(instance.Services.GetRequiredService<IFleetParticipation>().Current);
        Assert.Equal(started, participant.FleetId);
        Assert.True(participant.ClientOnly);
    }

    // --- And when it still happens: two fleets that really were started ------------------------------------------

    /// <summary>
    /// The rule above makes this rare, not impossible — two started fleets over two of your own pilots still leave
    /// <c>_ActingFleetId</c> with nothing to pick. The run does go solo then, but no longer without a word: going
    /// solo unannounced is the whole of what ET-165 reported.
    /// </summary>
    [AvaloniaFact]
    public void WithSeveralStartedFleets_TheRunHasNoFleet_AndTheWindowSaysWhy()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IFleetParticipation>().Set([
            new FleetParticipant(Owner, 11, ClientOnly: true, Owner),
            new FleetParticipant(Owner, 22, ClientOnly: true, Owner),
        ]);

        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        window.StartManualRun(DateTime.UtcNow);

        Assert.Null(window.FleetId);
        Assert.True(window.HasFleetNotice);
        Assert.Contains("2", window.FleetNoticeText, StringComparison.Ordinal);
        Assert.Contains("not shared", window.FleetNoticeText, StringComparison.OrdinalIgnoreCase);

        // And on screen, not only in the view model: a property nobody paints is the same silence in a new place.
        var view = new ActivityWindow(window) { Width = 560, Height = 620 };
        view.Show();
        Dispatcher.UIThread.RunJobs();

        TextBlock notice = view.FindControl<TextBlock>("FleetNoticeText")
                           ?? throw new InvalidOperationException("the fleet notice was not rendered");
        Assert.True(notice.IsVisible);
        Assert.Equal(window.FleetNoticeText, notice.Text);
        Assert.NotNull(view.CaptureRenderedFrame());
        view.Close();
    }

    /// <summary>The counter-proof: one fleet is not a question, so the run is filed under it and the line stays
    /// off. Without this the notice could be permanently on and the test above would still pass.</summary>
    [AvaloniaFact]
    public void WithOneStartedFleet_TheRunIsFiledUnderIt_AndThereIsNoNotice()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Owner, 11, ClientOnly: true, Owner)]);

        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        window.StartManualRun(DateTime.UtcNow);

        Assert.Equal(11, window.FleetId);
        Assert.False(window.HasFleetNotice);
    }

    /// <summary>
    /// Creates the fleet the way the client does — <see cref="ClientFleetService.CreateLocalFleetAsync"/>, which
    /// leaves it Forming — and then puts it at the activation under test. Set on the entity rather than through
    /// StartFleetCommand so all three values are reachable from one place; the refresher reads the repository, so
    /// this is the same input either route produces.
    /// </summary>
    private static async Task<long> _CreateLocalFleetAsync(TestClientInstance instance, FleetActivation activation)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Owner));

        Result<long> created = await instance.Services.GetRequiredService<ClientFleetService>()
            .CreateLocalFleetAsync("HF", null, Owner);
        Assert.True(created.IsSuccess);

        using IServiceScope scope = instance.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        FleetEntity fleet = await repository.GetAsync(created.Value)
                            ?? throw new InvalidOperationException("the fleet just created was not found");
        fleet.Activation = activation;
        await repository.UpdateAsync(fleet);

        return created.Value;
    }

    private static FleetInfo _ServerFleet(FleetActivation activation) => new(
        ServerFleetId, "Alpha Op", null, FleetVisibility.InviteOnly, FleetState.Active, Owner,
        null, null, DateTimeOffset.UnixEpoch, activation);
}
