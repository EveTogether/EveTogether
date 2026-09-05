using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-127 AC-3 — one counter-proof: the jump count must name where it counted from, and its absence must read as
/// "not fetched" rather than as a silent zero. Both halves are exercised against the same registered escalation, so
/// the only thing that changes between them is the ESI double — never a real network call (AC-1's whole saving
/// would be moot if this ticket's one remaining ESI call went live in a test).
/// </summary>
public sealed class EscalationJumpDistanceTests
{
    [AvaloniaFact]
    public async Task JumpsNameTheirAnchor_AndStayVisibleAsAnEmptyStateWhenTheyCannotBeRead()
    {
        var sde = new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30003867, "Ervekam", 0.69));
        using var harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddSingleton<ISdeAccessor>(sde));
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Site);
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);

        harness.Dialogs.OnShowEscalationDialog = dialog =>
        {
            dialog.SiteQuery = "Sansha Refuge";
            dialog.DestinationSystem = "Ervekam";
            dialog.RemainingTimeText = "1:00:00";
            dialog.RegisterCommand.Execute(null);
            return Task.FromResult(true);
        };
        await model.RegisterEscalationCommand.ExecuteAsync(null);
        await model.SaveRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery());
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);

        // A route of 7 systems (origin, 5 gates, destination) is 6 jumps.
        var reachable = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            esi: new FakeRouteEsiClient([30000001, 1, 2, 3, 4, 5, 30003867]),
            locations: new FakeLocationClient(30000001));
        await reachable.LoadAsync();
        Assert.Equal("6 jumps from here", reachable.EscalationJumpsText);
        Assert.Null(reachable.EscalationJumpsEmptyText);

        // ESI unreachable: the line must say so — not fall silent (which reads as "no destination") and not show a
        // bare/zero count (which reads as a measurement that came out at nothing).
        var unreachable = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            esi: new ThrowingEsiClient(), locations: new FakeLocationClient(30000001));
        await unreachable.LoadAsync();
        Assert.Null(unreachable.EscalationJumpsText);
        Assert.NotNull(unreachable.EscalationJumpsEmptyText);
    }

    private sealed class FakeRouteEsiClient(int[] route) : IEsiClient
    {
        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<T>.Ok((T)(object)route));
    }

    private sealed class ThrowingEsiClient : IEsiClient
    {
        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.Network, "connection refused")));
    }

    private sealed class FakeLocationClient(int solarSystemId) : IEsiLocationClient
    {
        public Task<EsiResult<EsiCharacterLocation>> GetLocationAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiCharacterLocation>.Ok(new EsiCharacterLocation { SolarSystemId = solarSystemId }));
    }
}
