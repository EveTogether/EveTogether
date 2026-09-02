using System.Reflection;
using EveUtils.Server.Api;
using EveUtils.Server.Api.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using EveUtils.Shared.Modules.Gamelog.Repositories;
using EveUtils.Shared.Modules.Gamelog.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
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
    private readonly ISharedFitRepository _fits;
    private readonly IServerAuthRepository _serverAuth;
    private readonly ICharacterMetricStateRepository _metrics;
    private readonly ServerApiQueries _queries;

    public ServerApiTests()
    {
        _fleets = new FleetRepository(_factory);
        _compositions = new FleetCompositionRepository(_factory);
        _fits = new SharedFitRepository(_factory);
        _serverAuth = new ServerAuthRepository(_factory);
        _metrics = new CharacterMetricStateRepository(_factory);
        _queries = new ServerApiQueries(_fleets, _compositions, _fits, _serverAuth, _metrics);
    }

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key with no owner: admin scope over all server data (ratified decision 3).</summary>
    private static readonly int? _AdminKey = null;

    /// <summary>A key issued to a character, which is scoped to what that character could discover anyway.</summary>
    private const int _OwnedKey = 90_000_007;

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

        ApiFleetDetail? detail = await _queries.GetFleetAsync(fleetId, _AdminKey, Ct);

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

        ApiFleetDetail? detail = await _queries.GetFleetAsync(fleetId, _AdminKey, Ct);

        Assert.NotNull(detail);
        Assert.Empty(detail.Wings);
        Assert.Empty(detail.Members);
        Assert.Null(detail.CompositionName);
    }

    [Fact]
    public async Task AFleetThatDoesNotExist_IsNull()
    {
        Assert.Null(await _queries.GetFleetAsync(4_242, _AdminKey, Ct));
    }

    /// <summary>The list is this server's fleet directory, not the join-me board: an invite-only fleet is on it
    /// too, because an ownerless key has admin scope over all server data (ratified decision 3).</summary>
    [Fact]
    public async Task TheFleetList_HoldsEveryFleetOnTheServer_IncludingInviteOnlyOnes()
    {
        long inviteOnly = await _StoreFleetAsync();
        long open = await _StoreFleetAsync(name: "Open roam", visibility: FleetVisibility.Public);
        await _StoreFleetAsync(name: "Old op", state: FleetState.Archived);

        IReadOnlyList<ApiFleetListItem> list = await _queries.GetFleetsAsync(_AdminKey, Ct);

        Assert.Equal([inviteOnly, open], list.Select(fleet => fleet.Id));
        Assert.Equal(["InviteOnly", "Public"], list.Select(fleet => fleet.Visibility));
    }

    /// <summary>
    /// A key issued to a character is not an admin key. Until the character-scoping of ratified decision 3 is
    /// built out, that key sees what the character could discover on the server anyway — the open fleets — and
    /// an invite-only fleet is not one of them.
    /// </summary>
    [Fact]
    public async Task AKeyScopedToACharacter_DoesNotSeeInviteOnlyFleets()
    {
        await _StoreFleetAsync();
        long open = await _StoreFleetAsync(name: "Open roam", visibility: FleetVisibility.Public);

        IReadOnlyList<ApiFleetListItem> list = await _queries.GetFleetsAsync(_OwnedKey, Ct);

        Assert.Equal([open], list.Select(fleet => fleet.Id));
    }

    /// <summary>And it cannot walk around the list by asking for the fleet by id.</summary>
    [Fact]
    public async Task AKeyScopedToACharacter_CannotFetchAnInviteOnlyFleetById()
    {
        long inviteOnly = await _StoreFleetAsync();
        long open = await _StoreFleetAsync(name: "Open roam", visibility: FleetVisibility.Public);

        Assert.Null(await _queries.GetFleetAsync(inviteOnly, _OwnedKey, Ct));
        Assert.NotNull(await _queries.GetFleetAsync(open, _OwnedKey, Ct));
        Assert.NotNull(await _queries.GetFleetAsync(inviteOnly, _AdminKey, Ct));
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

    // --- Fits ---

    [Fact]
    public async Task SharedFits_AreVisibleToEveryKey_AndReturnTheirFullPayload()
    {
        await _fits.AddAsync(new SharedFit
        {
            EsiFittingId = 7,
            Name = "Vindicator",
            ShipTypeId = 17_740,
            RawJson = "{\"ship_type_id\":17740}",
            SharedByCharacterId = 90_000_001,
            SharedByCharacterName = "Rin",
            SharedAt = DateTimeOffset.UnixEpoch
        }, Ct);

        IReadOnlyList<ApiFit> list = await _queries.GetFitsAsync(Ct);
        ApiFit? detail = await _queries.GetFitAsync(1, Ct);

        ApiFit fit = Assert.Single(list);
        Assert.Equal(90_000_001, fit.SharedByCharacterId);
        Assert.Equal("{\"ship_type_id\":17740}", Assert.IsType<string>(detail?.RawJson));
    }

    // --- Characters ---

    [Fact]
    public async Task SyncedCharacters_AreOwnerScoped_AndExposeOnlyPublicIdentity()
    {
        await _StoreSyncedCharacterAsync(_OwnedKey, "Rin");
        await _StoreSyncedCharacterAsync(90_000_008, "Vela");

        IReadOnlyList<ApiCharacter> own = await _queries.GetCharactersAsync(_OwnedKey, Ct);
        IReadOnlyList<ApiCharacter> all = await _queries.GetCharactersAsync(_AdminKey, Ct);

        Assert.Equal([_OwnedKey], own.Select(character => character.Id));
        Assert.Equal([_OwnedKey, 90_000_008], all.Select(character => character.Id));
        Assert.Null(await _queries.GetCharacterAsync(90_000_008, _OwnedKey, Ct));
        Assert.Equal(["Id", "Name"], typeof(ApiCharacter).GetProperties().Select(property => property.Name));
    }

    // --- Metrics ---

    [Fact]
    public async Task CharacterMetrics_AreOwnerScoped_AndExcludeOrphanedRows()
    {
        await _StoreSyncedCharacterAsync(_OwnedKey, "Rin");
        await _StoreSyncedCharacterAsync(90_000_008, "Vela");
        await _metrics.UpsertAsync("Rin", 100, 2, "{\"Veldspar\":7}", Ct);
        await _metrics.UpsertAsync("Vela", 200, 3, "{\"Scordite\":9}", Ct);
        await _metrics.UpsertAsync("Orphan", 300, 4, "{}", Ct);

        IReadOnlyList<ApiCharacterMetric> own = await _queries.GetMetricsAsync(_OwnedKey, Ct);
        IReadOnlyList<ApiCharacterMetric> all = await _queries.GetMetricsAsync(_AdminKey, Ct);

        Assert.Equal([_OwnedKey], own.Select(metric => metric.CharacterId));
        Assert.Equal([_OwnedKey, 90_000_008], all.Select(metric => metric.CharacterId));
        Assert.Equal("{\"Veldspar\":7}", Assert.Single(own).MinedJson);
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
    [InlineData("api/v1/fits", true)]
    [InlineData("api/v1/characters/{id}", true)]
    [InlineData("api/v1/metrics", true)]
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

    private Task _StoreSyncedCharacterAsync(int characterId, string name) =>
        _serverAuth.UpsertSyncedAsync(characterId, name, new EncryptedToken([1], [2], [3]), cancellationToken: Ct);

    private static FitReference _Fit(int shipTypeId, string fitName) => new()
    {
        ShipTypeId = shipTypeId,
        FitName = fitName,
        RawJson = "{\"name\":\"fit\"}",
        ContentHash = "0123456789abcdef"
    };
}
