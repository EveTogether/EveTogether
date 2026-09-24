using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi.Testing;
using EveUtils.Client.Formatting;
using EveUtils.Client.Killmails;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EveUtils.Client.Esi.Testing.EsiTestResponses;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-340: a killmail parsed from pasted clipboard text (no id, no hash) shows as a provisional row until the real
/// ESI killmail — from the feed or a pasted link — confirms and replaces it. One test per acceptance criterion,
/// using the two fixtures from ET-338's grooming comment verbatim.
/// </summary>
public sealed class ProvisionalKillmailTests : IDisposable
{
    private const int CormorantNavyIssueTypeId = 17722;
    private const int CapsuleTypeId = 670;

    private static readonly string Fixture1 = string.Join("\n",
        "2026.09.19 20:31:53",
        "",
        "Victim: Jithran",
        "Corp: Rivals of Promises",
        "Alliance: EVE Workbench Consortium",
        "Faction: Unknown",
        "Destroyed: Cormorant Navy Issue",
        "System: N-RAEL",
        "Security: 0.0",
        "Damage Taken: 5246",
        "",
        "Involved parties:",
        "",
        "Name: Kahla Rhal (laid the final blow)",
        "Security: 5.00",
        "Corp: University of Caille",
        "Alliance: None",
        "Faction: None",
        "Ship: Naga",
        "Weapon: 425mm Railgun II",
        "Damage Done: 3780",
        "",
        "Name: Foxy Rin",
        "Security: 1.1",
        "Corp: Pasika.UA",
        "Alliance: UA Fleets",
        "Faction: None",
        "Ship: Hawk",
        "Weapon: Scourge Rage Rocket",
        "Damage Done: 1466",
        "",
        "Destroyed items:",
        "",
        "Light Neutron Blaster II ",
        "Light Neutron Blaster II ",
        "Null S, Qty: 15990 (Cargo)",
        "Null S, Qty: 78 ",
        "Small Hybrid Burst Aerator II ",
        "Initiated Compact Warp Scrambler ",
        "IFFA Compact Damage Control ",
        "Medium Ancillary Shield Booster ",
        "Small Thermal Shield Reinforcer I ",
        "Cap Booster 100 ",
        "Small EM Shield Reinforcer I ",
        "Cap Booster 100, Qty: 42 (Cargo)",
        "Medium Ancillary Shield Booster ",
        "Null S, Qty: 78 ",
        "Void S, Qty: 1000 (Cargo)",
        "Null S, Qty: 78 ",
        "",
        "Dropped items:",
        "",
        "Null S, Qty: 78 ",
        "1MN Y-S8 Compact Afterburner ",
        "Light Neutron Blaster II ",
        "Null S, Qty: 78 ",
        "Light Neutron Blaster II ",
        "Light Neutron Blaster II ",
        "Cap Booster 100 ",
        "Magnetic Field Stabilizer I");

    private static readonly string Fixture2 = string.Join("\n",
        "2026.09.19 20:32:46",
        "",
        "Victim: Jithran",
        "Corp: Rivals of Promises",
        "Alliance: EVE Workbench Consortium",
        "Faction: Unknown",
        "Destroyed: Capsule",
        "System: N-RAEL",
        "Security: 0.0",
        "Damage Taken: 461",
        "",
        "Involved parties:",
        "",
        "Name: Kahla Rhal (laid the final blow)",
        "Security: 5.00",
        "Corp: University of Caille",
        "Alliance: None",
        "Faction: None",
        "Ship: Naga",
        "Weapon: 425mm Railgun II",
        "Damage Done: 461",
        "",
        "Name: Foxy Rin",
        "Security: 1.1",
        "Corp: Pasika.UA",
        "Alliance: UA Fleets",
        "Faction: None",
        "Ship: Hawk",
        "Weapon: Warp Scrambler II",
        "Damage Done: 0");

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "provisional-killmail-test-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

    private readonly TestClientInstance _instance = TestClientInstance.Create(services =>
        services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(CormorantNavyIssueTypeId, "Cormorant Navy Issue", groupId: 420, categoryId: 6)
            .Add(CapsuleTypeId, "Capsule", groupId: 29, categoryId: 6)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Criterion 1. Red if duplicate item lines are deduplicated instead of summed, or if (Cargo) and
    /// non-cargo stacks of the same item are merged.</summary>
    [Fact]
    public void Parser_Fixture1_ParsesTimeVictimShipAttackersAndItemTotals()
    {
        KillmailTextCapture? capture = KillmailTextParser.Parse(Fixture1);

        Assert.NotNull(capture);
        Assert.Equal(new DateTime(2026, 9, 19, 20, 31, 53, DateTimeKind.Utc), capture.KillmailTimeUtc);
        Assert.Equal("Jithran", capture.VictimName);
        Assert.Equal("Cormorant Navy Issue", capture.VictimShipName);
        Assert.True(_instance.Services.GetRequiredService<ISdeAccessor>().TryGetTypeId(capture.VictimShipName, out int shipTypeId));
        Assert.Equal(CormorantNavyIssueTypeId, shipTypeId);
        Assert.Equal(2, capture.Attackers.Count);
        Assert.Equal("Kahla Rhal", capture.Attackers.Single(attacker => attacker.FinalBlow).Name);
        Assert.Equal(234, capture.DestroyedItems.Single(item => item.Name == "Null S" && !item.IsCargo).Quantity);
        Assert.Equal(15990, capture.DestroyedItems.Single(item => item.Name == "Null S" && item.IsCargo).Quantity);
        Assert.Equal(1, capture.DestroyedItems.Single(item => item.Name == "Small Hybrid Burst Aerator II").Quantity);
    }

    /// <summary>Criterion 2. Red if a missing item section throws instead of parsing as empty.</summary>
    [Fact]
    public void Parser_Fixture2_ParsesWithoutItemSections_AndAZeroDamageAttacker()
    {
        KillmailTextCapture? capture = KillmailTextParser.Parse(Fixture2);

        Assert.NotNull(capture);
        Assert.Equal("Capsule", capture.VictimShipName);
        Assert.Empty(capture.DestroyedItems);
        Assert.Empty(capture.DroppedItems);
        Assert.Equal(0, capture.Attackers.Single(attacker => attacker.Name == "Foxy Rin").DamageDone);
    }

    /// <summary>Criterion 3. Red if "None"/"Unknown" show up as literal text, or a trailing space keeps two
    /// destroyed-item lines from aggregating under the same name.</summary>
    [Fact]
    public void Parser_NoneAndUnknownBecomeNull_AndTrailingSpacesNeverChangeAnItemName()
    {
        KillmailTextCapture? capture = KillmailTextParser.Parse(Fixture1);

        Assert.NotNull(capture);
        Assert.Null(capture.VictimFactionName); // "Faction: Unknown"
        KillmailTextAttacker kahla = capture.Attackers.Single(attacker => attacker.Name == "Kahla Rhal");
        Assert.Null(kahla.AllianceName); // "Alliance: None"
        Assert.Null(kahla.FactionName); // "Faction: None"
        // Both destroyed-item "Light Neutron Blaster II " lines carry a trailing space in the fixture — if the
        // parser failed to trim it, this exact name would never match and the two lines would never aggregate.
        Assert.Equal(2, capture.DestroyedItems.Single(item => item.Name == "Light Neutron Blaster II" && !item.IsCargo).Quantity);
    }

    /// <summary>Criterion 4. Red if the provisional row is missing from the overview, links to a run, or moves
    /// TOTAL ISK.</summary>
    [Fact]
    public async Task ProvisionalKillmail_ShowsInTheOverview_MarkedAndExcludedFromRunLinkAndTotals()
    {
        const int CharacterId = 500;
        await _instance.Services.GetRequiredService<IProvisionalKillmailRepository>().AddAsync(new ProvisionalKillmail
        {
            Id = Guid.NewGuid(),
            CharacterId = CharacterId,
            KillmailTimeUtc = new DateTime(2026, 9, 19, 20, 31, 53, DateTimeKind.Utc),
            VictimName = "Jithran",
            VictimShipTypeId = CormorantNavyIssueTypeId,
            RawText = Fixture1,
            CreatedAtUtc = DateTime.UtcNow
        }, Ct);
        KillmailsOverviewViewModel viewModel = new(_instance.Services.GetRequiredService<IDispatcher>(),
            new RecordingDialogService(), _instance.Services,
            [new Character("Test Pilot", CharacterId, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails])],
            (_, _) => Task.CompletedTask);

        await viewModel.LoadAsync(Ct);

        KillmailRowViewModel row = Assert.Single(viewModel.Days.SelectMany(day => day.Rows));
        Assert.True(row.ShowProvisionalChip);
        Assert.Equal("FROM CLIPBOARD · WAITING FOR ESI", row.ProvisionalChipText);
        Assert.Null(row.RunId);
        Assert.Equal(IskFormat.Compact(0m), viewModel.IskDestroyedText);
        Assert.Equal(IskFormat.Compact(0m), viewModel.IskLostText);
    }

    /// <summary>Criterion 5. Red if two rows remain, or if a kill (the own character as attacker, a stranger as
    /// victim) never replaces its provisional row because the victim's name only comes from the local ET-336
    /// entity-name cache rather than the character registry.</summary>
    [Theory]
    [InlineData(500, "Own Pilot", 600, "Stranger", 500, 600, "Own Pilot")]  // loss: own character is the victim
    [InlineData(500, "Own Pilot", 600, "Stranger", 600, 500, "Stranger")]   // kill: own character is the attacker
    public async Task RealMail_SameTimeVictimAndShip_ReplacesTheProvisionalRow(
        int ownCharacterId, string ownCharacterName, int strangerId, string strangerName, int victimId, int attackerId, string provisionalVictimName)
    {
        DateTime timeUtc = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        await _instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character(ownCharacterName, ownCharacterId), Ct);
        await _instance.Services.GetRequiredService<IKillmailEntityNameRepository>().UpsertAsync(
            new KillmailEntityName { Id = strangerId, Kind = KillmailEntityKind.Character, Name = strangerName, RefreshedAtUtc = DateTime.UtcNow }, Ct);
        await _instance.Services.GetRequiredService<IProvisionalKillmailRepository>().AddAsync(new ProvisionalKillmail
        {
            Id = Guid.NewGuid(), CharacterId = ownCharacterId, KillmailTimeUtc = timeUtc, VictimName = provisionalVictimName,
            VictimShipTypeId = CormorantNavyIssueTypeId, RawText = Fixture1, CreatedAtUtc = DateTime.UtcNow
        }, Ct);
        _routes["/killmails/1/hash1/"] = () => Json(200, $$"""
            {"killmail_id":1,"killmail_time":"2026-09-20T12:00:00Z","solar_system_id":30000142,
             "victim":{"character_id":{{victimId}},"ship_type_id":{{CormorantNavyIssueTypeId}},"damage_taken":1,"items":[]},
             "attackers":[{"character_id":{{attackerId}},"damage_done":1,"final_blow":true}]}
            """);
        var (client, _, _) = _Pipeline(EsiAuthorization.Authorized("token"));

        await new EsiKillmailImporter(client, _instance.Services.GetRequiredService<ILocalKillmailRepository>(),
            _instance.Services.GetRequiredService<IServiceScopeFactory>()).ImportOneAsync(1, "hash1", Ct);

        Assert.Empty(await _instance.Services.GetRequiredService<IProvisionalKillmailRepository>().GetForCharacterAsync(ownCharacterId, Ct));
    }

    /// <summary>Criterion 6. Red if a real mail is matched on time and victim alone, ignoring the ship.</summary>
    [Fact]
    public async Task RealMail_SameTimeAndVictim_ButDifferentShip_DoesNotReplaceTheProvisionalRow()
    {
        const int OwnCharacterId = 500;
        DateTime timeUtc = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        await _instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Own Pilot", OwnCharacterId), Ct);
        await _instance.Services.GetRequiredService<IProvisionalKillmailRepository>().AddAsync(new ProvisionalKillmail
        {
            Id = Guid.NewGuid(), CharacterId = OwnCharacterId, KillmailTimeUtc = timeUtc, VictimName = "Own Pilot",
            VictimShipTypeId = CormorantNavyIssueTypeId, RawText = Fixture1, CreatedAtUtc = DateTime.UtcNow
        }, Ct);
        _routes["/killmails/1/hash1/"] = () => Json(200, $$"""
            {"killmail_id":1,"killmail_time":"2026-09-20T12:00:00Z","solar_system_id":30000142,
             "victim":{"character_id":{{OwnCharacterId}},"ship_type_id":{{CapsuleTypeId}},"damage_taken":1,"items":[]},
             "attackers":[{"character_id":601,"damage_done":1,"final_blow":true}]}
            """);
        var (client, _, _) = _Pipeline(EsiAuthorization.Authorized("token"));

        await new EsiKillmailImporter(client, _instance.Services.GetRequiredService<ILocalKillmailRepository>(),
            _instance.Services.GetRequiredService<IServiceScopeFactory>()).ImportOneAsync(1, "hash1", Ct);

        Assert.Single(await _instance.Services.GetRequiredService<IProvisionalKillmailRepository>().GetForCharacterAsync(OwnCharacterId, Ct));
    }

    /// <summary>Criterion 7. Red if a row for the active character is stored even though the text names none of
    /// the pilot's own characters.</summary>
    [Fact]
    public async Task Import_TextWithNoOwnCharacter_IsRefusedAndStoresNothing()
    {
        ProvisionalKillmailImportResult result =
            await _instance.Services.GetRequiredService<ProvisionalKillmailImporter>().ImportAsync(Fixture1, Ct);

        Assert.Equal(ProvisionalKillmailImportStatus.NoOwnCharacter, result.Status);
        Assert.Equal(0, result.StoredCount);
    }

    /// <summary>Criterion 8. Red if the fake <see cref="IEsiClient"/> sees a call while parsing or storing the text.</summary>
    [Fact]
    public async Task Import_NeverCallsEsi()
    {
        var recordingEsi = new RecordingEsiClient();
        using TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IEsiClient>(recordingEsi);
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(CormorantNavyIssueTypeId, "Cormorant Navy Issue", groupId: 420, categoryId: 6));
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Jithran", 500), Ct);

        ProvisionalKillmailImportResult result = await instance.Services.GetRequiredService<ProvisionalKillmailImporter>().ImportAsync(Fixture1, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, recordingEsi.Calls);
    }

    public void Dispose()
    {
        _instance.Dispose();
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    private (IEsiClient Client, FileEsiCacheStore Store, StubHttpMessageHandler Stub) _Pipeline(EsiAuthorization authorization)
    {
        var stub = new StubHttpMessageHandler((request, _) => _routes[request.RequestUri?.PathAndQuery ?? ""]());
        var store = new FileEsiCacheStore(_cacheDirectory);
        var cache = new EsiCacheHandler(store, new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance)) { InnerHandler = stub };
        var client = new EsiClient(new SingleClientHttpFactory(new HttpClient(cache)), new FakeEsiTokenProvider(authorization),
            new EsiOutageDetector(new EsiAvailabilityState()), NullLogger<EsiClient>.Instance);
        return (client, store, stub);
    }

    private sealed class RecordingEsiClient : IEsiClient
    {
        public int Calls { get; private set; }

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ServerError, "should never be called", 500)));
        }
    }
}
