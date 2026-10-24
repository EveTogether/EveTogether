using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Client.WorldMap;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Map;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-525: setting the in-game autopilot from the map. The target choice and the query-string mapping are pure;
/// the rest drives the map view model against a recording autopilot client.</summary>
public sealed class MapAutopilotTests
{
    private const int Jita = 30000142;
    private const int Kaelen = 91000001;
    private const int Mira = 91000002;
    private const string Scope = AutopilotScopeCatalog.WriteWaypoint;

    private static MapAutopilotCandidate Candidate(int id, string name, bool hasScope = true, bool isOffline = false) =>
        new(id, name, hasScope, isOffline);

    [Fact]
    public void Resolve_FollowedCharacter_IsTheTargetEvenWithOthersAllowed()
    {
        var followed = Candidate(Kaelen, "Kaelen");

        MapAutopilotTarget target = MapAutopilotTarget.Resolve(followed, [followed, Candidate(Mira, "Mira")]);

        Assert.Equal(MapAutopilotPlan.Send, target.Plan);
        Assert.Equal(Kaelen, Assert.Single(target.Candidates).CharacterId);
    }

    [Fact]
    public void Resolve_FollowedCharacterWithoutScope_OffersAllowForThatCharacterOnly()
    {
        var followed = Candidate(Kaelen, "Kaelen", hasScope: false);

        MapAutopilotTarget target = MapAutopilotTarget.Resolve(followed, [followed, Candidate(Mira, "Mira")]);

        Assert.Equal(MapAutopilotPlan.Allow, target.Plan);
        Assert.Equal(Kaelen, Assert.Single(target.Candidates).CharacterId);
    }

    [Fact]
    public void Resolve_NotFollowing_OneCharacterWithScope_IsChosenWithoutPicker()
    {
        MapAutopilotTarget target = MapAutopilotTarget.Resolve(null, [Candidate(Kaelen, "Kaelen", hasScope: false), Candidate(Mira, "Mira")]);

        Assert.Equal(MapAutopilotPlan.Send, target.Plan);
        Assert.Equal(Mira, Assert.Single(target.Candidates).CharacterId);
    }

    [Fact]
    public void Resolve_NotFollowing_SeveralWithScope_PicksAmongThemWithOfflineLast()
    {
        MapAutopilotTarget target = MapAutopilotTarget.Resolve(null,
        [
            Candidate(Kaelen, "Aria", isOffline: true),
            Candidate(Mira, "Zed"),
            Candidate(3, "Bo"),
            Candidate(4, "Cy", hasScope: false)
        ]);

        Assert.Equal(MapAutopilotPlan.Choose, target.Plan);
        Assert.Equal(["Bo", "Zed", "Aria"], target.Candidates.Select(candidate => candidate.Name));
        Assert.True(target.Candidates[^1].Opacity < target.Candidates[0].Opacity);
    }

    [Fact]
    public void Resolve_NobodyHasScope_OneCharacterAllows_SeveralPickWhoAllows_NoneHasNoCharacter()
    {
        Assert.Equal(MapAutopilotPlan.Allow, MapAutopilotTarget.Resolve(null, [Candidate(Kaelen, "Kaelen", hasScope: false)]).Plan);
        Assert.Equal(MapAutopilotPlan.ChooseToAllow,
            MapAutopilotTarget.Resolve(null, [Candidate(Kaelen, "Kaelen", false), Candidate(Mira, "Mira", false)]).Plan);
        Assert.Equal(MapAutopilotPlan.NoCharacter, MapAutopilotTarget.Resolve(null, []).Plan);
    }

    [Theory]
    [InlineData(WaypointMode.SetDestination, "/ui/autopilot/waypoint/?destination_id=30000142&add_to_beginning=false&clear_other_waypoints=true")]
    [InlineData(WaypointMode.AddWaypoint, "/ui/autopilot/waypoint/?destination_id=30000142&add_to_beginning=false&clear_other_waypoints=false")]
    public async Task SetWaypoint_MapsTheModeToTheQueryAndSendsOneBodylessPostForThatCharacter(WaypointMode mode, string path)
    {
        var esi = new RecordingEsiClient();

        EsiResult result = await new EsiAutopilotClient(esi).SetWaypointAsync(Kaelen, Jita, mode, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        EsiRequest request = Assert.Single(esi.Requests);
        Assert.Equal(path, request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Null(request.Body);
        Assert.Equal(Kaelen, request.CharacterId);
        Assert.Equal([Scope], request.Scopes);
    }

    [Fact]
    public void ScopeCatalog_DeclaresTheScopeAsOptInForTheClient()
    {
        EsiScopeRequirement requirement = Assert.Single(AutopilotScopeCatalog.Catalog.Requirements);

        Assert.Equal(Scope, requirement.Scope);
        Assert.True(requirement.OptIn);
        Assert.Equal(EsiScopeTarget.Client, requirement.Target);
    }

    [Theory]
    [InlineData(EsiErrorKind.AuthPending, "try again in a moment")]
    [InlineData(EsiErrorKind.AuthRequired, "sign in again")]
    [InlineData(EsiErrorKind.ScopeMissing, "not allowed")]
    [InlineData(EsiErrorKind.RateLimited, "Try again later")]
    [InlineData(EsiErrorKind.ServerError, "boom")]
    public void Failure_ExplainsEachKindInPlainWords(EsiErrorKind kind, string expected)
    {
        string text = MapAutopilotMessages.Failure(EsiError.Of(kind, "boom"), "Kaelen");

        Assert.Contains("Kaelen", text);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task SetDestination_WhileFollowingACharacter_PostsOnceForThatCharacter()
    {
        using var world = await World.OpenAsync(kaelenAllowed: true, miraAllowed: true);
        world.Follow(Kaelen);
        world.Select(Jita);

        await world.Model.SetDestinationCommand.ExecuteAsync(null);

        (int characterId, int systemId, WaypointMode mode) = Assert.Single(world.Autopilot.Calls);
        Assert.Equal((Kaelen, Jita, WaypointMode.SetDestination), (characterId, systemId, mode));
        Assert.StartsWith("Destination set for Kaelen Voss: Jita", world.Model.AutopilotMessage);
        Assert.False(world.Model.IsAutopilotChoosing);
    }

    [AvaloniaFact]
    public async Task AddWaypoint_PostsOnceWithTheAddMode()
    {
        using var world = await World.OpenAsync(kaelenAllowed: true, miraAllowed: false);
        world.Select(Jita);

        await world.Model.AddWaypointCommand.ExecuteAsync(null);

        (int characterId, _, WaypointMode mode) = Assert.Single(world.Autopilot.Calls);
        Assert.Equal((Kaelen, WaypointMode.AddWaypoint), (characterId, mode));
        Assert.StartsWith("Waypoint added for Kaelen Voss", world.Model.AutopilotMessage);
    }

    [AvaloniaFact]
    public async Task SetDestination_NotFollowing_SeveralAllowed_AsksWhichAndSendsOnlyForThePick()
    {
        using var world = await World.OpenAsync(kaelenAllowed: true, miraAllowed: true);
        world.Select(Jita);

        await world.Model.SetDestinationCommand.ExecuteAsync(null);

        Assert.True(world.Model.IsAutopilotChoosing);
        Assert.Empty(world.Autopilot.Calls);
        MapAutopilotCandidate pick = world.Model.AutopilotChoices.Single(choice => choice.CharacterId == Mira);

        await world.Model.ChooseAutopilotCharacterCommand.ExecuteAsync(pick);

        Assert.Equal(Mira, Assert.Single(world.Autopilot.Calls).CharacterId);
        Assert.False(world.Model.IsAutopilotChoosing);
    }

    [AvaloniaFact]
    public async Task SetDestination_WhileFollowingAFleet_IgnoresTheEarlierFollowedCharacter()
    {
        using var world = await World.OpenAsync(kaelenAllowed: true, miraAllowed: true);
        world.Follow(Kaelen);
        world.Model.SetFollowModeCommand.Execute(MapFollowMode.Fleet);
        world.Select(Jita);

        await world.Model.SetDestinationCommand.ExecuteAsync(null);

        Assert.True(world.Model.IsAutopilotChoosing);
        Assert.Empty(world.Autopilot.Calls);
    }

    [AvaloniaFact]
    public async Task SetDestination_NobodyAllowed_StartsAllowForTheOnlyFollowedCharacterAndPostsNothing()
    {
        using var world = await World.OpenAsync(kaelenAllowed: false, miraAllowed: false);
        world.Follow(Mira);
        world.Select(Jita);

        await world.Model.SetDestinationCommand.ExecuteAsync(null);

        Assert.Empty(world.Autopilot.Calls);
        Assert.Equal([(Mira, Scope)], world.Allowed);
        Assert.Contains("ALLOW", world.Model.AutopilotButtonText);
    }

    [AvaloniaTheory]
    [InlineData(EsiErrorKind.ScopeMissing, true)]
    [InlineData(EsiErrorKind.AuthRequired, true)]
    [InlineData(EsiErrorKind.AuthPending, false)]
    [InlineData(EsiErrorKind.ServerError, false)]
    [InlineData(EsiErrorKind.RateLimited, false)]
    public async Task SetDestination_WhenEsiFails_ShowsAMessageAndOnlyAsksToAllowWhereThatHelps(EsiErrorKind kind, bool asksToAllow)
    {
        using var world = await World.OpenAsync(kaelenAllowed: true, miraAllowed: false);
        world.Autopilot.Failure = EsiError.Of(kind, "nope");
        world.Select(Jita);

        await world.Model.SetDestinationCommand.ExecuteAsync(null);

        Assert.Single(world.Autopilot.Calls);
        Assert.True(world.Model.HasAutopilotMessage);
        Assert.Equal(asksToAllow, world.Allowed.Count == 1);
        Assert.False(world.Model.IsAutopilotBusy);
    }

    [AvaloniaFact]
    public async Task SetDestination_DoubleClickWhileTheCallRuns_SendsOneRequest()
    {
        using var world = await World.OpenAsync(kaelenAllowed: true, miraAllowed: false);
        var gate = new TaskCompletionSource();
        world.Autopilot.Gate = gate.Task;
        world.Select(Jita);

        Task first = world.Model.SetDestinationCommand.ExecuteAsync(null);
        Task second = world.Model.SetDestinationCommand.ExecuteAsync(null);
        Assert.True(world.Model.IsAutopilotBusy);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(world.Autopilot.Calls);
        Assert.False(world.Model.IsAutopilotBusy);
    }

    [AvaloniaFact]
    public async Task SetDestination_WithNothingSelected_SendsNothing()
    {
        using var world = await World.OpenAsync(kaelenAllowed: true, miraAllowed: false);

        await world.Model.SetDestinationCommand.ExecuteAsync(null);

        Assert.Empty(world.Autopilot.Calls);
    }

    private sealed class RecordingEsiClient : IEsiClient
    {
        public List<EsiRequest> Requests { get; } = [];

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(EsiResult<T>.Ok(default!));
        }
    }

    private sealed class RecordingAutopilot : IEsiAutopilotClient
    {
        public List<(int CharacterId, int SolarSystemId, WaypointMode Mode)> Calls { get; } = [];

        public EsiError? Failure { get; set; }

        public Task? Gate { get; set; }

        public async Task<EsiResult> SetWaypointAsync(int characterId, int solarSystemId, WaypointMode mode, CancellationToken cancellationToken = default)
        {
            Calls.Add((characterId, solarSystemId, mode));
            if (Gate is not null)
                await Gate;
            return Failure is { } error ? EsiResult.Fail(error) : EsiResult.Ok();
        }
    }

    private sealed class World : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly MapTrailRecorder _recorder;

        private World(TestClientInstance instance, MapTrailRecorder recorder, MapViewModel model)
        {
            _instance = instance;
            _recorder = recorder;
            Model = model;
        }

        public MapViewModel Model { get; }

        public required RecordingAutopilot Autopilot { get; init; }

        public required List<(int CharacterId, string Scope)> Allowed { get; init; }

        public static async Task<World> OpenAsync(bool kaelenAllowed, bool miraAllowed)
        {
            TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(MapFixture.Sde()));
            var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
            await registry.AddOrUpdateAsync(new Character("Kaelen Voss", Kaelen, kaelenAllowed ? [Scope] : []));
            await registry.AddOrUpdateAsync(new Character("Mira Tane", Mira, miraAllowed ? [Scope] : []));

            var positions = instance.Services.GetRequiredService<FleetPositionSource>();
            var recorder = new MapTrailRecorder(positions, TimeProvider.System);
            var autopilot = new RecordingAutopilot();
            var allowed = new List<(int, string)>();
            var model = new MapViewModel(instance.Services.GetRequiredService<IDispatcher>(), registry, positions, recorder,
                instance.Services.GetRequiredService<IMapFleetSource>(), TimeProvider.System,
                autopilot: autopilot, allowScope: (characterId, scope) =>
                {
                    allowed.Add((characterId, scope));
                    return Task.CompletedTask;
                });
            await model.LoadAsync();
            return new World(instance, recorder, model) { Autopilot = autopilot, Allowed = allowed };
        }

        public void Follow(int characterId)
        {
            Model.SetFollowModeCommand.Execute(MapFollowMode.Character);
            Model.FollowCharacterCommand.Execute(Model.Characters.Single(row => row.CharacterId == characterId));
        }

        public void Select(int solarSystemId)
        {
            Assert.True(Model.Graph!.TryGetIndex(solarSystemId, out int index));
            Model.SelectedIndex = index;
        }

        public void Dispose()
        {
            Model.Dispose();
            _recorder.Dispose();
            _instance.Dispose();
        }
    }
}
