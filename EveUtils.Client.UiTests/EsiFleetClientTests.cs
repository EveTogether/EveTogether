using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// of the ESI fleet-coupling: the read client over the metered pivot + the fleet_boss_id precheck. The key
/// guard is that a non-boss never hits <c>/fleets/{id}/</c> (its 404 would burn the error-limit budget → ban risk),
/// resolved cheaply from the 60s-cached per-member <c>/characters/{id}/fleet/</c>.
/// </summary>
public class EsiFleetClientTests
{
    [Fact]
    public async Task GetCharacterFleet_ParsesDto_AndDetectsBoss()
    {
        var esi = new FakeEsiClient();
        esi.Responses["/characters/100/fleet/"] = new EsiCharacterFleet { FleetId = 7, FleetBossId = 100, WingId = -1, SquadId = -1 };

        var result = await new EsiFleetClient(esi).GetCharacterFleetAsync(100, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value!.FleetId);
        Assert.True(result.Value.IsBoss(100));
        Assert.False(result.Value.IsBoss(200));
    }

    [Fact]
    public async Task GetMembers_AsBoss_ReturnsRoster_AfterPrecheck()
    {
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{boss}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };
        esi.Responses[$"/fleets/{fleetId}/members/"] =
            new[] { new EsiFleetMember { CharacterId = 100 }, new EsiFleetMember { CharacterId = 200 } };

        var result = await new EsiFleetClient(esi).GetMembersAsync(fleetId, boss, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Length);
        Assert.Contains($"/fleets/{fleetId}/members/", esi.RequestedPaths);
    }

    [Fact]
    public async Task GetMembers_AsNonBoss_FailsPrecheck_WithoutHittingFleetsEndpoint()
    {
        const int actor = 200;
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{actor}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi).GetMembersAsync(fleetId, actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain($"/fleets/{fleetId}/members/", esi.RequestedPaths); // the 404 burn was avoided
    }

    [Fact]
    public async Task GetWings_WhenActorInDifferentFleet_FailsPrecheck()
    {
        const int actor = 100;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{actor}/fleet/"] = new EsiCharacterFleet { FleetId = 999, FleetBossId = actor };

        var result = await new EsiFleetClient(esi).GetWingsAsync(fleetId: 7, actingCharacterId: actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("/fleets/7/wings/", esi.RequestedPaths);
    }

    [Fact]
    public void EsiCharacterFleet_DeserializesSnakeCaseFields()
    {
        const string json = """{"fleet_id":7,"fleet_boss_id":100,"role":"fleet_commander","wing_id":-1,"squad_id":-1}""";

        var dto = JsonSerializer.Deserialize<EsiCharacterFleet>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(dto);
        Assert.Equal(7, dto!.FleetId);
        Assert.Equal(100, dto.FleetBossId);
        Assert.Equal("fleet_commander", dto.Role);
        Assert.True(dto.IsBoss(100));
    }

    [Fact]
    public async Task SetFleetSettings_AsBoss_SendsPut_AfterPrecheck()
    {
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{boss}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi)
            .SetFleetSettingsAsync(fleetId, boss, motd: "Form up on me", isFreeMove: true, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains($"/fleets/{fleetId}/", esi.RequestedPaths);
    }

    [Fact]
    public async Task SetFleetSettings_AsNonBoss_FailsPrecheck_WithoutSendingPut()
    {
        const int actor = 200;
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{actor}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi)
            .SetFleetSettingsAsync(fleetId, actor, motd: "nope", isFreeMove: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain($"/fleets/{fleetId}/", esi.RequestedPaths); // a non-boss write never reaches /fleets/{id}/
    }

    [Fact]
    public async Task MoveMember_AsBoss_SendsPut_AfterPrecheck()
    {
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{boss}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi)
            .MoveMemberAsync(fleetId, memberCharacterId: 200, "squad_member", wingId: 10, squadId: 20, boss, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains($"/fleets/{fleetId}/members/200/", esi.RequestedPaths);
    }

    [Fact]
    public async Task MoveMember_AsNonBoss_FailsPrecheck_WithoutSendingPut()
    {
        const int actor = 200;
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{actor}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi)
            .MoveMemberAsync(fleetId, memberCharacterId: 200, "squad_member", wingId: 10, squadId: 20, actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain($"/fleets/{fleetId}/members/200/", esi.RequestedPaths); // the 404 burn was avoided
    }

    [Fact]
    public async Task KickMember_AsBoss_SendsDelete_AfterPrecheck()
    {
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{boss}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi)
            .KickMemberAsync(fleetId, memberCharacterId: 200, boss, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains($"/fleets/{fleetId}/members/200/", esi.RequestedPaths);
    }

    [Fact]
    public async Task CreateWing_AsBoss_ParsesWingId()
    {
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{boss}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };
        esi.JsonResponses[$"/fleets/{fleetId}/wings/"] = """{"wing_id":5001}""";

        var result = await new EsiFleetClient(esi).CreateWingAsync(fleetId, boss, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(5001L, result.Value);
    }

    [Fact]
    public async Task CreateSquad_AsBoss_ParsesSquadId()
    {
        const int boss = 100;
        const long fleetId = 7;
        const long wingId = 5001;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{boss}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };
        esi.JsonResponses[$"/fleets/{fleetId}/wings/{wingId}/squads/"] = """{"squad_id":6001}""";

        var result = await new EsiFleetClient(esi).CreateSquadAsync(fleetId, wingId, boss, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(6001L, result.Value);
    }

    [Fact]
    public async Task InviteMember_AsBoss_SendsPost_AfterPrecheck()
    {
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{boss}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi)
            .InviteMemberAsync(fleetId, characterId: 200, "squad_member", wingId: 10, squadId: 20, boss, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains($"/fleets/{fleetId}/members/", esi.RequestedPaths);
    }

    [Fact]
    public async Task InviteMember_AsNonBoss_FailsPrecheck_WithoutSendingPost()
    {
        const int actor = 200;
        const int boss = 100;
        const long fleetId = 7;
        var esi = new FakeEsiClient();
        esi.Responses[$"/characters/{actor}/fleet/"] = new EsiCharacterFleet { FleetId = fleetId, FleetBossId = boss };

        var result = await new EsiFleetClient(esi)
            .InviteMemberAsync(fleetId, characterId: 300, "squad_member", wingId: 10, squadId: 20, actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain($"/fleets/{fleetId}/members/", esi.RequestedPaths);
    }

    /// <summary>An <see cref="IEsiClient"/> that answers typed GETs from a per-path table (or a per-path JSON body, so a
    /// write's response can be parsed into a private DTO via the generic <c>T</c>), treats any other non-GET as a
    /// 204-style write success, and records every path hit.</summary>
    private sealed class FakeEsiClient : IEsiClient
    {
        public Dictionary<string, object?> Responses { get; } = new();
        public Dictionary<string, string> JsonResponses { get; } = new();
        public List<string> RequestedPaths { get; } = new();

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default)
        {
            RequestedPaths.Add(request.Path);
            if (JsonResponses.TryGetValue(request.Path, out var json))
            {
                var parsed = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Task.FromResult(parsed is null
                    ? EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ParseError, "null body"))
                    : EsiResult<T>.Ok(parsed));
            }
            if (request.Method != HttpMethod.Get)
                return Task.FromResult(EsiResult<T>.Ok(default!)); // write → 204 No Content
            return Task.FromResult(Responses.TryGetValue(request.Path, out var value) && value is T typed
                ? EsiResult<T>.Ok(typed)
                : EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.NotFound, $"no stub for {request.Path}", 404)));
        }
    }
}
