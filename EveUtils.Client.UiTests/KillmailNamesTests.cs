using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Killmails;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Character, corporation and alliance names for a killmail (ET-336): the corp/alliance shown is the killmail's
/// own, never a character's current one; a resolved name is cached in <see cref="KillmailEntityName"/> and refreshed
/// only past the configured interval; NPC corporations and a client's own characters never reach ESI.
/// </summary>
public class KillmailNamesTests
{
    [Fact]
    public async Task HydrateAsync_CorporationId_ShowsCorporationFromKillmailNotCharactersCurrentCorp()
    {
        var resolver = new RecordingAffiliationResolver();
        resolver.CorporationNames[100] = "Corp X"; // the corporation id stored on the killmail itself

        var characterLookup = new RecordingCharacterLookup();
        characterLookup.Results[77] = new ExternalCharacterInfo(77, "Pilot", Corp: "Corp Y", Alliance: null, Exists: true); // the character's corp today

        var names = NewKillmailNames(characterLookup: characterLookup, resolver: resolver);

        await names.HydrateAsync([77], [100], [], TestContext.Current.CancellationToken);

        Assert.Equal("Corp X", names.NameOf(100)); // the killmail's own corp, never the character's corp today
        Assert.Equal("Pilot", names.NameOf(77));
    }

    [Fact]
    public async Task HydrateAsync_SecondReadOfSameKillmail_MakesNoFurtherEsiCalls()
    {
        var resolver = new RecordingAffiliationResolver();
        resolver.CorporationNames[100] = "Corp X";
        var repository = new InMemoryKillmailEntityNameRepository();

        var firstRead = NewKillmailNames(resolver: resolver, repository: repository);
        await firstRead.HydrateAsync([], [100], [], TestContext.Current.CancellationToken);

        var secondRead = NewKillmailNames(resolver: resolver, repository: repository); // a fresh instance, same persisted table
        await secondRead.HydrateAsync([], [100], [], TestContext.Current.CancellationToken);

        Assert.Equal(1, resolver.CorporationCalls);
        Assert.Equal("Corp X", secondRead.NameOf(100));
    }

    [Theory]
    [InlineData(2, 1, true)]    // row 2 days old, interval 1 day → refreshed
    [InlineData(0, 1, false)]   // row from today, interval 1 day → not refreshed
    [InlineData(31, null, true)]  // row 31 days old, no setting key → default 30 days → refreshed
    [InlineData(29, null, false)] // row 29 days old, no setting key → default 30 days → not refreshed
    public async Task HydrateAsync_RefreshesOnlyPastTheConfiguredInterval(int rowAgeDays, int? refreshDaysSetting, bool expectRefresh)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var repository = new InMemoryKillmailEntityNameRepository();
        await repository.UpsertAsync(new KillmailEntityName
        {
            Id = 100,
            Kind = KillmailEntityKind.Corporation,
            Name = "Old Name",
            RefreshedAtUtc = clock.GetUtcNow().UtcDateTime.AddDays(-rowAgeDays)
        }, TestContext.Current.CancellationToken);
        var resolver = new RecordingAffiliationResolver();
        resolver.CorporationNames[100] = "New Name";
        var settings = new InMemorySettingRepository(refreshDaysSetting);

        var names = NewKillmailNames(resolver: resolver, repository: repository, settings: settings, clock: clock);
        await names.HydrateAsync([], [100], [], TestContext.Current.CancellationToken);

        Assert.Equal(expectRefresh ? 1 : 0, resolver.CorporationCalls);
        Assert.Equal(expectRefresh ? "New Name" : "Old Name", names.NameOf(100));
    }

    [Fact]
    public async Task HydrateAsync_NpcCorporationAndOwnCharacter_NeverReachEsi()
    {
        var resolver = new RecordingAffiliationResolver(); // no corp registered: a call here would resolve to null, proving it happened
        var sde = new FakeSdeAccessor().AddNpcCorporation(1000125, "Guristas Pirates");
        var characterLookup = new RecordingCharacterLookup();
        var ownCharacterNames = new Dictionary<int, string> { [90000001] = "My Alt" };

        var names = NewKillmailNames(ownCharacterNames: ownCharacterNames, characterLookup: characterLookup, resolver: resolver, sde: sde);
        await names.HydrateAsync([90000001], [1000125], [], TestContext.Current.CancellationToken);

        Assert.Equal(0, resolver.CorporationCalls);
        Assert.Equal(0, characterLookup.Calls);
        Assert.Equal("Guristas Pirates", names.NameOf(1000125));
        Assert.Equal("My Alt", names.NameOf(90000001));
    }

    [Fact]
    public async Task HydrateAsync_UnknownCharacterId_StoresNoNameAndDoesNotRetryWithinInterval()
    {
        var characterLookup = new RecordingCharacterLookup();
        characterLookup.Results[42] = ExternalCharacterInfo.Unknown(42); // ESI 404
        var repository = new InMemoryKillmailEntityNameRepository();

        var firstRead = NewKillmailNames(characterLookup: characterLookup, repository: repository);
        await firstRead.HydrateAsync([42], [], [], TestContext.Current.CancellationToken); // must not throw

        Assert.Equal("42", firstRead.NameOf(42));
        Assert.Equal(1, characterLookup.Calls);

        var secondRead = NewKillmailNames(characterLookup: characterLookup, repository: repository);
        await secondRead.HydrateAsync([42], [], [], TestContext.Current.CancellationToken);

        Assert.Equal("42", secondRead.NameOf(42));
        Assert.Equal(1, characterLookup.Calls); // not asked again within the refresh interval
    }

    private static KillmailNames NewKillmailNames(
        IReadOnlyDictionary<int, string>? ownCharacterNames = null,
        IExternalCharacterLookup? characterLookup = null,
        IEsiAffiliationResolver? resolver = null,
        ISdeAccessor? sde = null,
        IKillmailEntityNameRepository? repository = null,
        ISettingRepository? settings = null,
        TimeProvider? clock = null) =>
        new(
            ownCharacterNames ?? new Dictionary<int, string>(),
            characterLookup ?? new RecordingCharacterLookup(),
            resolver ?? new RecordingAffiliationResolver(),
            sde ?? new FakeSdeAccessor(),
            repository ?? new InMemoryKillmailEntityNameRepository(),
            settings ?? new InMemorySettingRepository(null),
            clock ?? TimeProvider.System);

    private sealed class RecordingAffiliationResolver : IEsiAffiliationResolver
    {
        public Dictionary<int, string> CorporationNames { get; } = new();
        public Dictionary<int, string> AllianceNames { get; } = new();
        public int CorporationCalls { get; private set; }
        public int AllianceCalls { get; private set; }

        public Task<EsiCharacterAffiliation?> ResolveAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<EsiCharacterAffiliation?>(null);

        public Task<string?> ResolveCorporationNameAsync(int corporationId, CancellationToken cancellationToken = default)
        {
            CorporationCalls++;
            return Task.FromResult(CorporationNames.GetValueOrDefault(corporationId));
        }

        public Task<string?> ResolveAllianceNameAsync(int allianceId, CancellationToken cancellationToken = default)
        {
            AllianceCalls++;
            return Task.FromResult(AllianceNames.GetValueOrDefault(allianceId));
        }
    }

    private sealed class RecordingCharacterLookup : IExternalCharacterLookup
    {
        public Dictionary<int, ExternalCharacterInfo> Results { get; } = new();
        public int Calls { get; private set; }

        public Task<ExternalCharacterInfo> LookupAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Results.TryGetValue(characterId, out var info) ? info : ExternalCharacterInfo.Unknown(characterId));
        }
    }

    private sealed class InMemoryKillmailEntityNameRepository : IKillmailEntityNameRepository
    {
        private readonly Dictionary<long, KillmailEntityName> _rows = new();

        public Task<IReadOnlyDictionary<long, KillmailEntityName>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<long, KillmailEntityName> found = ids
                .Where(_rows.ContainsKey)
                .ToDictionary(id => id, id => _rows[id]);
            return Task.FromResult(found);
        }

        public Task UpsertAsync(KillmailEntityName entry, CancellationToken cancellationToken = default)
        {
            _rows[entry.Id] = entry;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemorySettingRepository(int? nameRefreshDays) : ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ClientSetting> settings = nameRefreshDays is { } days
                ? [new ClientSetting { Key = KillmailNames.NameRefreshDaysSettingKey, Value = days.ToString() }]
                : [];
            return Task.FromResult(settings);
        }

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ET-336: a settable clock so the refresh-interval test can move the row's age without a real sleep.
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
