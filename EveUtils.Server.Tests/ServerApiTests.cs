using System.Reflection;
using EveUtils.Server.Api;
using EveUtils.Server.Api.Dtos;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-119: the first data endpoints. The bridge reads the same repositories the rest of the server reads and maps
/// to public DTOs — so what is tested here is that a fleet comes back whole, and that nothing an external consumer
/// should never see can reach the wire.
/// </summary>
public class ServerApiTests : IDisposable
{
    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly IFleetRepository _fleets;
    private readonly IFleetCompositionRepository _compositions;
    private readonly ServerApiQueries _queries;

    public ServerApiTests()
    {
        _fleets = new FleetRepository(_factory);
        _compositions = new FleetCompositionRepository(_factory);
        _queries = new ServerApiQueries(_fleets, _compositions);
    }

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Fleets ---

    /// <summary>
    /// The acceptance case: a fleet with two wings and a filled roster comes back complete, not just its header.
    /// Squads hang under their own wing and each member keeps the placement the roster gave it.
    /// </summary>
    [Fact]
    public async Task AFleetDetail_CarriesItsWings_SquadsAndRoster()
    {
        long compositionId = await _StoreCompositionAsync();
        long fleetId = await _StoreFleetAsync(compositionId: compositionId);
        (long wingId, long squadId) = await _StoreWingAsync(fleetId, "Assault", "Alpha");
        (long secondWingId, long secondSquadId) = await _StoreWingAsync(fleetId, "Logistics", "Bravo");
        await _StoreMemberAsync(fleetId, 90_000_001, wingId, squadId, FleetRole.FleetCommander, fitName: "Vindicator");
        await _StoreMemberAsync(fleetId, 90_000_002, secondWingId, secondSquadId, FleetRole.SquadMember);

        ApiFleetDetail? detail = await _queries.GetFleetAsync(fleetId, Ct);

        Assert.NotNull(detail);
        Assert.Equal("Home defence", detail.Name);
        Assert.Equal("Forming", detail.Activation);
        Assert.Equal("InviteOnly", detail.Visibility);
        Assert.Equal("Shield doctrine", detail.CompositionName);

        Assert.Equal(["Assault", "Logistics"], detail.Wings.Select(wing => wing.Name));
        Assert.Equal(["Alpha"], detail.Wings[0].Squads.Select(squad => squad.Name));
        Assert.Equal(["Bravo"], detail.Wings[1].Squads.Select(squad => squad.Name));

        Assert.Equal(2, detail.Members.Count);
        ApiFleetMember commander = detail.Members.Single(member => member.CharacterId == 90_000_001);
        Assert.Equal(wingId, commander.WingId);
        Assert.Equal(squadId, commander.SquadId);
        Assert.Equal("FleetCommander", commander.Role);
        Assert.Equal("Vindicator", commander.FitName);
        Assert.Equal(17_740, commander.ShipTypeId);
        Assert.Null(detail.Members.Single(member => member.CharacterId == 90_000_002).FitName);
    }

    /// <summary>An empty fleet is a fleet, not a half-answer: the collections come back empty, never null.</summary>
    [Fact]
    public async Task AFleetWithoutStructure_ComesBackWithEmptyCollections()
    {
        long fleetId = await _StoreFleetAsync();

        ApiFleetDetail? detail = await _queries.GetFleetAsync(fleetId, Ct);

        Assert.NotNull(detail);
        Assert.Empty(detail.Wings);
        Assert.Empty(detail.Members);
        Assert.Null(detail.CompositionName);
    }

    [Fact]
    public async Task AFleetThatDoesNotExist_IsNull()
    {
        Assert.Null(await _queries.GetFleetAsync(4_242, Ct));
    }

    /// <summary>The list is this server's fleet directory, not the join-me board: an invite-only fleet is on it
    /// too, because an ownerless key has admin scope over all server data (ratified decision 3).</summary>
    [Fact]
    public async Task TheFleetList_HoldsEveryFleetOnTheServer_IncludingInviteOnlyOnes()
    {
        long inviteOnly = await _StoreFleetAsync();
        long open = await _StoreFleetAsync(name: "Open roam", visibility: FleetVisibility.Public);
        await _StoreFleetAsync(name: "Old op", state: FleetState.Archived);

        IReadOnlyList<ApiFleetListItem> list = await _queries.GetFleetsAsync(Ct);

        Assert.Equal([inviteOnly, open], list.Select(fleet => fleet.Id));
        Assert.Equal(["InviteOnly", "Public"], list.Select(fleet => fleet.Visibility));
    }

    // --- Compositions ---

    [Fact]
    public async Task ACompositionDetail_CarriesItsRolesAndFitEntries()
    {
        long compositionId = await _StoreCompositionAsync();
        long roleId = await _compositions.AddRoleAsync(
            new FleetCompositionRole { CompositionId = compositionId, RoleName = "Logi", GroupMinCount = 4 }, Ct);
        await _compositions.AddEntryAsync(new FleetCompositionEntry
        {
            RoleId = roleId,
            EntryMinCount = 2,
            Fit = _Fit(11_987, "Guardian - shield")
        }, Ct);

        ApiCompositionDetail? detail = await _queries.GetCompositionAsync(compositionId, Ct);

        Assert.NotNull(detail);
        Assert.Equal("Shield doctrine", detail.Name);
        ApiCompositionRole role = Assert.Single(detail.Roles);
        Assert.Equal("Logi", role.RoleName);
        Assert.Equal(4, role.GroupMinCount);
        ApiCompositionEntry entry = Assert.Single(role.Entries);
        Assert.Equal(2, entry.EntryMinCount);
        Assert.Equal(11_987, entry.ShipTypeId);
        Assert.Equal("Guardian - shield", entry.FitName);
    }

    [Fact]
    public async Task ACompositionThatDoesNotExist_IsNull()
    {
        Assert.Null(await _queries.GetCompositionAsync(4_242, Ct));
    }

    /// <summary>The library row reports how many fleets fly the doctrine — including zero for one nobody uses.</summary>
    [Fact]
    public async Task TheCompositionList_CountsTheFleetsCoupledToEachDoctrine()
    {
        long used = await _StoreCompositionAsync();
        long unused = await _StoreCompositionAsync("Armor doctrine");
        await _StoreFleetAsync(compositionId: used);
        await _StoreFleetAsync(name: "Second op", compositionId: used);

        IReadOnlyList<ApiCompositionListItem> list = await _queries.GetCompositionsAsync(Ct);

        Assert.Equal(2, list.Single(item => item.Id == used).FleetCount);
        Assert.Equal(0, list.Single(item => item.Id == unused).FleetCount);
    }

    // --- Nothing leaks ---

    /// <summary>
    /// The counter-proof for "no answer carries a token, a session token or a hash": put a field with such a name
    /// on any API DTO and this turns red. Names rather than values, because the fault this guards against is a
    /// field nobody meant to publish being added — long before a test with a real token would ever run.
    /// </summary>
    [Fact]
    public void NoApiDto_HasAFieldThatSoundsLikeASecret()
    {
        string[] forbidden = ["token", "hash", "secret", "password", "credential"];

        foreach (PropertyInfo property in _ApiDtos.SelectMany(dto => dto.GetProperties()))
        {
            Assert.DoesNotContain(forbidden,
                word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The other half of the same fault: a DTO that lets an entity through publishes every field that entity has,
    /// now and whenever someone adds one. So every property is a value, or another API DTO, and nothing else.
    /// </summary>
    [Fact]
    public void EveryApiDtoProperty_IsAValueOrAnotherApiDto()
    {
        foreach (Type dto in _ApiDtos)
        {
            foreach (PropertyInfo property in dto.GetProperties())
                Assert.True(_IsPublishable(property.PropertyType),
                    $"{dto.Name}.{property.Name} publishes {property.PropertyType.Name}, which is not an API DTO.");
        }
    }

    /// <summary>The DTOs the API can answer with: everything in the Dtos namespace plus M0's whoami shape.</summary>
    private static readonly Type[] _ApiDtos =
    [
        .. typeof(ApiHealthResponse).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(ApiHealthResponse).Namespace),
        typeof(ApiWhoAmIResponse)
    ];

    private static bool _IsPublishable(Type type)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual.IsPrimitive || actual.IsEnum || actual == typeof(string)
            || actual == typeof(decimal) || actual == typeof(DateTimeOffset))
            return true;
        if (_ApiDtos.Contains(actual)) return true;
        if (actual.IsGenericType && actual.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return _IsPublishable(actual.GetGenericArguments()[0]);
        return false;
    }

    // --- The contract Scalar shows ---

    /// <summary>Both ways of presenting a key are in the document, and the query form carries the log-leak
    /// warning that ratified decision 8 attached to keeping it.</summary>
    [Fact]
    public void TheOpenApiDocument_DescribesBothWaysOfSendingTheKey()
    {
        var document = new OpenApiDocument();

        ServerApiDocs.DescribeApiKeyAuth(document);

        IOpenApiSecurityScheme header = document.Components!.SecuritySchemes![ServerApiDocs.SecuritySchemeId];
        Assert.Equal(SecuritySchemeType.ApiKey, header.Type);
        Assert.Equal(ParameterLocation.Header, header.In);
        Assert.Equal(ApiKeyAuthentication.HeaderName, header.Name);

        IOpenApiSecurityScheme query = document.Components.SecuritySchemes[ServerApiDocs.QuerySchemeId];
        Assert.Equal(ParameterLocation.Query, query.In);
        Assert.Equal(ApiKeyAuthentication.QueryName, query.Name);
        Assert.Contains("log", query.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The public document covers the v1 API and nothing else — the host's own routes stay out of a contract
    /// anyone can read without a key.
    /// </summary>
    [Theory]
    [InlineData("api/v1/fleets", true)]
    [InlineData("/api/v1/health", true)]
    [InlineData("api/server/scopes", false)]
    [InlineData("account/login", false)]
    [InlineData("backup/download", false)]
    [InlineData(null, false)]
    public void TheOpenApiDocument_CoversTheV1ApiAndNothingElse(string? relativePath, bool included)
    {
        Assert.Equal(included, ServerApiDocs.DescribesTheV1Api(relativePath));
    }

    /// <summary>A gated operation asks for the key in the contract; a keyless one does not claim to.</summary>
    [Fact]
    public void TheOpenApiDocument_MarksGatedOperations_AndLeavesAnonymousOnesOpen()
    {
        var gated = new OpenApiOperation();
        var anonymous = new OpenApiOperation();

        ServerApiDocs.RequireApiKey(gated, []);
        ServerApiDocs.RequireApiKey(anonymous, [new AllowAnonymousAttribute()]);

        Assert.Contains(Assert.Single(gated.Security!),
            requirement => requirement.Key.Reference.Id == ServerApiDocs.SecuritySchemeId);
        Assert.True(anonymous.Security is null or { Count: 0 });
    }

    // --- Fixtures ---

    private async Task<long> _StoreFleetAsync(
        string name = "Home defence",
        FleetVisibility visibility = FleetVisibility.InviteOnly,
        FleetState state = FleetState.Active,
        long? compositionId = null) =>
        await _fleets.AddAsync(new FleetEntity
        {
            Name = name,
            Description = "the standing home fleet",
            Visibility = visibility,
            State = state,
            Activation = FleetActivation.Forming,
            CreatorCharacterId = 90_000_000,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
            FleetCompositionId = compositionId
        }, Ct);

    private async Task<(long WingId, long SquadId)> _StoreWingAsync(long fleetId, string wing, string squad)
    {
        long wingId = await _fleets.AddWingAsync(new FleetWing { FleetId = fleetId, Name = wing }, Ct);
        long squadId = await _fleets.AddSquadAsync(new FleetSquad { WingId = wingId, Name = squad }, Ct);
        return (wingId, squadId);
    }

    private Task _StoreMemberAsync(
        long fleetId, int characterId, long wingId, long squadId, FleetRole role, string? fitName = null) =>
        _fleets.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = characterId,
            WingId = wingId,
            SquadId = squadId,
            Role = role,
            JoinTime = DateTimeOffset.UtcNow,
            AssignedFit = fitName is null ? null : _Fit(17_740, fitName)
        }, Ct);

    private async Task<long> _StoreCompositionAsync(string name = "Shield doctrine") =>
        await _compositions.AddAsync(new FleetComposition
        {
            Name = name,
            Description = "the standing doctrine",
            OwnerCharacterId = 90_000_000,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, Ct);

    private static FitReference _Fit(int shipTypeId, string fitName) => new()
    {
        ShipTypeId = shipTypeId,
        FitName = fitName,
        RawJson = "{\"name\":\"fit\"}",
        ContentHash = "0123456789abcdef"
    };
}
