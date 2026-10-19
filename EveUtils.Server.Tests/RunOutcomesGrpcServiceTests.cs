using System.Globalization;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Server.RunOutcomes;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.RunOutcomes;
using EveUtils.Shared.Modules.RunOutcomes.Commands;
using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Events;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Runtime;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StoredLootLine = EveUtils.Shared.Modules.RunOutcomes.Entities.RunOutcomeLootLine;
using WireLootLine = EveUtils.Grpc.RunOutcomeLootLine;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-514: the anonymous run outcomes a server collects for the earnings statistic, through the real gRPC service,
/// dispatcher, handlers and a SQLite store. What is stored and handed out carries no pilot, system or signature; one
/// fleet activity is one outcome; a pilot's opt-out and tombstone take exactly their own outcomes; the per-pilot cap and
/// the window are applied on the server; the control panel's switch refuses with a <c>PERMISSION_DENIED</c> reply.
/// </summary>
public sealed class RunOutcomesGrpcServiceTests : IDisposable
{
    private const int Jithran = 90250177;
    private const int Raymond = 90000002;
    private const int Ayla = 90000003;
    private const int SleeperCache = 7720;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "et514-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;
    private readonly RunOutcomePseudonyms _pseudonyms;
    private readonly List<RunOutcomesChangedEvent> _signals = [];
    private readonly IDisposable _subscription;
    private readonly Dictionary<int, string> _tokens = [];

    public RunOutcomesGrpcServiceTests()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddCqrs();
        services.AddEventBus();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddSingleton<IRuntimeContext>(new RuntimeContext(ExecutionHost.Server));
        services.AddSingleton<IDbContextFactory<SharedDbContext>>(_factory);
        services.AddSingleton<IDbContextFactory<ServerDbContext>>(_factory);
        _provider = services.BuildServiceProvider();
        _pseudonyms = new RunOutcomePseudonyms(_dataDirectory);
        _subscription = _provider.GetRequiredService<IEventBus>().Subscribe<RunOutcomesChangedEvent>((signal, _) =>
        {
            _signals.Add(signal);
            return Task.CompletedTask;
        });
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _provider.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ShareOutcome_StoresThePseudonymsOnly_NeverTheCharacterIdOrGroupCode()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();

        RunOutcomeActionReply reply = await service.ShareOutcome(_Share("FLEET-HF-Z6U3", _Today(-1)), _Context(Jithran));

        Assert.True(reply.Accepted, reply.Message);
        RunOutcome stored = Assert.Single(await _StoredAsync());
        Assert.Equal(_pseudonyms.ContributorBucket(Jithran), stored.ContributorBucket);
        Assert.DoesNotContain(Jithran.ToString(CultureInfo.InvariantCulture), stored.ContributorBucket);
        Assert.Equal(_pseudonyms.ActivityId("FLEET-HF-Z6U3"), stored.ActivityId);
        Assert.DoesNotContain("HF-Z6U3", stored.ActivityId);
        Assert.Equal(TimeSpan.Zero, stored.CompletedOn.TimeOfDay);
        Assert.Equal(2, stored.LootLines.Count);
    }

    /// <summary>The table is the consent: a new column here is new data shared about a pilot, which needs a decision,
    /// not a migration that slips through.</summary>
    [Fact]
    public void Schema_RunOutcomeTables_HoldNoPilotSystemSignatureOrTime()
    {
        using ServerDbContext db = ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContext();

        string[] outcomeColumns = _Columns(db, typeof(RunOutcome));
        string[] lineColumns = _Columns(db, typeof(StoredLootLine));

        Assert.Equal(
            new[] { "AbyssalTier", "AbyssalWeather", "ActivityId", "CompletedOn", "ContributorBucket", "DurationSeconds", "FixedIsk",
                "FleetSize", "Id", "KeyCode", "LootBasis", "MissionLevel", "NormalizedName", "OpenUnrecognisedLootCount",
                "Revision", "SiteTypeSource", "StatsKind", "TypeId" },
            outcomeColumns);
        Assert.Equal(new[] { "Id", "Kind", "Quantity", "RunOutcomeId", "TypeId" }, lineColumns);
    }

    [Fact]
    public async Task GetOutcomesAndLoot_NeverReturnACharacterIdBucketOrName()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        await service.ShareOutcome(_Share("solo-1", _Today(-2)), _Context(Jithran));
        await service.ShareOutcome(_Share("solo-2", _Today(-1)), _Context(Raymond));
        ServerCallContext reader = _Context(Ayla);

        GetOutcomesReply outcomes = await service.GetOutcomes(_Get(_Today(-90)), reader);
        GetOutcomeLootReply loot = await service.GetOutcomeLoot(new GetOutcomeLootRequest { Key = _SiteKey(), Since = _Today(-90) }, reader);
        ListOutcomeKeysReply keys = await service.ListOutcomeKeys(new ListOutcomeKeysRequest { Since = _Today(-90) }, reader);

        Assert.True(outcomes.Accepted && loot.Accepted && keys.Accepted);
        Assert.Equal(2, Assert.Single(outcomes.Activities).Outcomes.Count);
        Assert.Equal(2, loot.Outcomes.Count);
        string[] answered = [outcomes.ToString(), loot.ToString(), keys.ToString()];
        string[] forbidden =
        [
            Jithran.ToString(CultureInfo.InvariantCulture), Raymond.ToString(CultureInfo.InvariantCulture),
            _pseudonyms.ContributorBucket(Jithran), _pseudonyms.ContributorBucket(Raymond),
            _pseudonyms.ActivityId("solo-1"), _pseudonyms.ActivityId("solo-2"), "solo-1", "Jithran", "Raymond"
        ];
        Assert.All(answered, text => Assert.All(forbidden, secret => Assert.DoesNotContain(secret, text)));
        Assert.All(new[] { RunOutcomeRow.Descriptor, RunOutcomeLoot.Descriptor, WireLootLine.Descriptor, ActivityOutcomes.Descriptor },
            descriptor => Assert.All(descriptor.Fields.InFieldNumberOrder(), field =>
                Assert.DoesNotMatch("(?i)character|pilot_(id|name)|bucket|contributor|activity_(id|ref)|system|signature", field.Name)));
    }

    [Fact]
    public async Task ShareOutcome_OneFleetActivitySharedByThreePilots_IsOneOutcome()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();

        RunOutcomeActionReply first = await service.ShareOutcome(_Share("FLEET-AB12", _Today(-1)), _Context(Jithran));
        RunOutcomeActionReply second = await service.ShareOutcome(_Share("FLEET-AB12", _Today(-1)), _Context(Raymond));
        RunOutcomeActionReply third = await service.ShareOutcome(_Share("FLEET-AB12", _Today(-1), revision: 5), _Context(Ayla));

        Assert.Equal((int)RunOutcomeShareStatus.Stored, first.Status);
        Assert.Equal((int)RunOutcomeShareStatus.SharedByAnother, second.Status);
        Assert.Equal((int)RunOutcomeShareStatus.SharedByAnother, third.Status);
        RunOutcome stored = Assert.Single(await _StoredAsync());
        Assert.Equal(_pseudonyms.ContributorBucket(Jithran), stored.ContributorBucket);
        Assert.Single(_signals);
    }

    [Fact]
    public async Task ShareOutcome_NewerRevisionReplacesTheOwnOutcome_AnOlderOneChangesNothing()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        await service.ShareOutcome(_Share("solo", _Today(-1), revision: 2), jithran);

        RunOutcomeActionReply stale = await service.ShareOutcome(_Share("solo", _Today(-1), revision: 1, fixedIsk: "1"), jithran);
        RunOutcomeActionReply updated = await service.ShareOutcome(_Share("solo", _Today(-1), revision: 3, fixedIsk: "42000000.50"), jithran);

        Assert.Equal((int)RunOutcomeShareStatus.Stale, stale.Status);
        Assert.Equal((int)RunOutcomeShareStatus.Updated, updated.Status);
        RunOutcome stored = Assert.Single(await _StoredAsync());
        Assert.Equal(42_000_000.50m, stored.FixedIsk);
        Assert.Equal(3, stored.Revision);
        Assert.Equal(2, stored.LootLines.Count);
        Assert.Equal(new[] { RunOutcomesChangeKind.Stored, RunOutcomesChangeKind.Updated }, _signals.Select(signal => signal.Data.Kind));
    }

    [Fact]
    public async Task WithdrawMyOutcomes_RemovesExactlyTheOwnOutcomesWithTheirLines()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        await service.ShareOutcome(_Share("j-1", _Today(-1)), jithran);
        await service.ShareOutcome(_Share("j-2", _Today(-2)), jithran);
        await service.ShareOutcome(_Share("r-1", _Today(-1)), _Context(Raymond));

        RunOutcomeActionReply reply = await service.WithdrawMyOutcomes(new WithdrawMyOutcomesRequest(), jithran);

        Assert.True(reply.Accepted, reply.Message);
        Assert.Equal(2, reply.Count);
        RunOutcome left = Assert.Single(await _StoredAsync());
        Assert.Equal(_pseudonyms.ContributorBucket(Raymond), left.ContributorBucket);
        Assert.Equal(2, await _LootLineCountAsync());
        Assert.Equal(RunOutcomesChangeKind.Withdrawn, _signals[^1].Data.Kind);
    }

    [Fact]
    public async Task ShareOutcome_Tombstone_RemovesTheOwnOutcomeAndItsLines_NotAFleetMates()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        await service.ShareOutcome(_Share("FLEET-AB12", _Today(-1), revision: 2), jithran);

        RunOutcomeActionReply byMate = await service.ShareOutcome(
            new ShareOutcomeRequest { ActivityRef = "FLEET-AB12", Revision = 9, Deleted = true }, _Context(Raymond));
        Assert.Equal((int)RunOutcomeShareStatus.SharedByAnother, byMate.Status);
        Assert.Single(await _StoredAsync());

        RunOutcomeActionReply byOwner = await service.ShareOutcome(
            new ShareOutcomeRequest { ActivityRef = "FLEET-AB12", Revision = 3, Deleted = true }, jithran);

        Assert.Equal((int)RunOutcomeShareStatus.Removed, byOwner.Status);
        Assert.Empty(await _StoredAsync());
        Assert.Equal(0, await _LootLineCountAsync());
        Assert.Equal(RunOutcomesChangeKind.Removed, _signals[^1].Data.Kind);
    }

    [Fact]
    public async Task SwitchOff_ShareAndReadsArePermissionDenied_WithdrawStillWorks()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        await service.ShareOutcome(_Share("j-1", _Today(-1)), jithran);
        _provider.GetRequiredService<IPermissionToggleStore>().SetEnabled(RunOutcomesModule.EnabledToggle, false);

        RunOutcomeActionReply share = await service.ShareOutcome(_Share("j-2", _Today(-1)), jithran);
        GetOutcomesReply read = await service.GetOutcomes(_Get(_Today(-90)), jithran);
        GetOutcomeLootReply loot = await service.GetOutcomeLoot(new GetOutcomeLootRequest { Key = _SiteKey(), Since = _Today(-90) }, jithran);
        ListOutcomeKeysReply keys = await service.ListOutcomeKeys(new ListOutcomeKeysRequest { Since = _Today(-90) }, jithran);
        RunOutcomeActionReply withdrawn = await service.WithdrawMyOutcomes(new WithdrawMyOutcomesRequest(), jithran);

        Assert.Equal((false, MessageCodes.PermissionDenied), (share.Accepted, share.Code));
        Assert.Equal((false, MessageCodes.PermissionDenied), (read.Accepted, read.Code));
        Assert.Empty(read.Activities);
        Assert.Equal((false, MessageCodes.PermissionDenied), (loot.Accepted, loot.Code));
        Assert.Equal((false, MessageCodes.PermissionDenied), (keys.Accepted, keys.Code));
        Assert.Equal((true, 1), (withdrawn.Accepted, withdrawn.Count));
        Assert.Empty(await _StoredAsync());
    }

    [Fact]
    public async Task GetOutcomes_FarmerAndCasual_CapsEachPilotAtTenRuns()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        ServerCallContext raymond = _Context(Raymond);
        for (int run = 0; run < 20; run++)
            await service.ShareOutcome(_Share($"j-{run}", _Today(-1 - run % 30)), jithran);
        for (int run = 0; run < 5; run++)
            await service.ShareOutcome(_Share($"r-{run}", _Today(-1 - run)), raymond);

        ActivityOutcomes activity = Assert.Single((await service.GetOutcomes(_Get(_Today(-90)), raymond)).Activities);
        OutcomeKeyCount count = Assert.Single((await service.ListOutcomeKeys(new ListOutcomeKeysRequest { Since = _Today(-90) }, raymond)).Keys);

        Assert.Equal(25, activity.Outcomes.Count);
        Assert.Equal(20, activity.Outcomes.Count(outcome => outcome.Weight == 0.5));
        Assert.Equal(5, activity.Outcomes.Count(outcome => outcome.Weight == 1));
        Assert.Equal(2, activity.PilotCount);
        Assert.Equal((25, 15d, 2), (count.RunCount, count.EffectiveRunCount, count.PilotCount));
    }

    [Fact]
    public async Task GetOutcomes_SiteRunsWithoutLootAndTooShort_WeighNothingAndDoNotFillTheCap()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        for (int run = 0; run < 10; run++)
            await service.ShareOutcome(_Share($"j-{run}", _Today(-1)), jithran);
        await service.ShareOutcome(_Share("j-uncounted", _Today(-1), lootBasis: LootBasis.None), jithran);
        await service.ShareOutcome(_Share("j-short", _Today(-1), durationSeconds: 59), jithran);

        RunOutcomeRow[] rows = [.. Assert.Single((await service.GetOutcomes(_Get(_Today(-90)), jithran)).Activities).Outcomes];

        Assert.Equal(12, rows.Length);
        Assert.Equal(10, rows.Count(row => row.Weight == 1));
        Assert.Single(rows, row => row is { LootBasis: (int)LootBasis.None, Weight: 0 });
        Assert.Single(rows, row => row is { DurationSeconds: 0, Weight: 0 });
    }

    [Fact]
    public async Task GetOutcomes_MissionRunWithoutLoot_StillCounts()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        RunOutcomeKey mission = new() { Kind = (int)ActivityStatsKind.Mission, TypeId = 2821, MissionLevel = 4 };

        await service.ShareOutcome(_Share("m-1", _Today(-1), lootBasis: LootBasis.None, key: mission), jithran);
        GetOutcomesReply reply = await service.GetOutcomes(new GetOutcomesRequest { Keys = { mission }, Since = _Today(-90) }, jithran);

        Assert.Equal(1, Assert.Single(Assert.Single(reply.Activities).Outcomes).Weight);
    }

    [Fact]
    public async Task GetOutcomes_Window_LeavesOutOlderOutcomesAndNeverReachesPast365Days()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        await service.ShareOutcome(_Share("recent", _Today(-10)), jithran);
        await service.ShareOutcome(_Share("older", _Today(-200)), jithran);
        await _SeedAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-380));

        GetOutcomesReply recent = await service.GetOutcomes(_Get(_Today(-90)), jithran);
        GetOutcomesReply widest = await service.GetOutcomes(_Get(_Today(-1000)), jithran);

        Assert.Equal(new[] { _Today(-10) }, Assert.Single(recent.Activities).Outcomes.Select(outcome => outcome.CompletedOn));
        Assert.Equal(new[] { _Today(-10), _Today(-200) },
            Assert.Single(widest.Activities).Outcomes.Select(outcome => outcome.CompletedOn).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task ShareOutcome_OutsideTheWindowOrUnreadable_IsRefusedAndStoresNothing()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);

        RunOutcomeActionReply tooOld = await service.ShareOutcome(_Share("old", _Today(-366)), jithran);
        RunOutcomeActionReply noKey = await service.ShareOutcome(
            _Share("nokey", _Today(-1), key: new RunOutcomeKey { Kind = (int)ActivityStatsKind.Site, Name = "  " }), jithran);
        RunOutcomeActionReply badLine = await service.ShareOutcome(_Share("bad", _Today(-1), quantity: 0), jithran);

        Assert.All(new[] { tooOld, noKey, badLine }, reply => Assert.Equal((false, MessageCodes.ValidationFailed), (reply.Accepted, reply.Code)));
        Assert.Empty(await _StoredAsync());
        Assert.Empty(_signals);
    }

    [Fact]
    public async Task ShareOutcome_UncataloguedSite_GroupsOnTheNormalisedName_AndTheDurationOnTheWholeMinute()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        ServerCallContext jithran = _Context(Jithran);
        RunOutcomeKey Named(string name) => new() { Kind = (int)ActivityStatsKind.Site, SiteTypeSource = (int)SiteTypeSource.Uncatalogued, Name = name };

        await service.ShareOutcome(_Share("a", _Today(-1), key: Named("Forgotten  Perimeter Gateway "), durationSeconds: 1199), jithran);
        await service.ShareOutcome(_Share("b", _Today(-1), key: Named("forgotten perimeter gateway")), jithran);
        ActivityOutcomes activity = Assert.Single((await service.GetOutcomes(
            new GetOutcomesRequest { Keys = { Named("FORGOTTEN PERIMETER GATEWAY") }, Since = _Today(-90) }, jithran)).Activities);

        Assert.Equal("forgotten perimeter gateway", activity.Key.Name);
        Assert.Equal(2, activity.Outcomes.Count);
        Assert.Contains(activity.Outcomes, outcome => outcome.DurationSeconds == 1140);
    }

    [Fact]
    public async Task PruneRunOutcomes_RemovesOutcomesPastTheCutoffWithTheirLines()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();
        await service.ShareOutcome(_Share("recent", _Today(-10)), _Context(Jithran));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _SeedAsync(today.AddDays(-401));

        using IServiceScope scope = _provider.CreateScope();
        Result<int> pruned = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new PruneRunOutcomesCommand(today.AddDays(-RunOutcomesModule.RetentionDays)), Cancellation);

        Assert.Equal(1, pruned.Value);
        Assert.Equal(today.AddDays(-10), DateOnly.FromDateTime(Assert.Single(await _StoredAsync()).CompletedOn));
        Assert.Equal(2, await _LootLineCountAsync());
        Assert.Equal(RunOutcomesChangeKind.Pruned, _signals[^1].Data.Kind);
    }

    [Fact]
    public async Task AnyCall_WithoutASession_IsUnauthenticated()
    {
        RunOutcomesGrpcService service = await _ServiceAsync();

        RpcException refused = await Assert.ThrowsAsync<RpcException>(() =>
            service.ShareOutcome(_Share("x", _Today(-1)), new HeadersOnlyCallContext([])));

        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
    }

    private async Task<RunOutcomesGrpcService> _ServiceAsync()
    {
        ServerAuthRepository authRepository = new(_factory);
        ServerSessionService sessions = new(authRepository, NullLogger<ServerSessionService>.Instance);
        foreach ((int characterId, string name) in new[] { (Jithran, "Jithran"), (Raymond, "Raymond"), (Ayla, "Ayla") })
        {
            var character = await authRepository.UpsertSyncedAsync(characterId, name, new EncryptedToken([1], [2], [3]), null, Cancellation);
            _tokens[characterId] = (await sessions.IssueAsync(character.Id, Cancellation)).AccessToken;
        }

        return new RunOutcomesGrpcService(sessions, _pseudonyms, _provider.GetRequiredService<IDispatcher>());
    }

    private ServerCallContext _Context(int characterId) =>
        new HeadersOnlyCallContext(new Metadata { { "authorization", $"Bearer {_tokens[characterId]}" } });

    private static RunOutcomeKey _SiteKey() =>
        new() { Kind = (int)ActivityStatsKind.Site, SiteTypeSource = (int)SiteTypeSource.Site, TypeId = SleeperCache };

    private static GetOutcomesRequest _Get(string since) => new() { Keys = { _SiteKey() }, Since = since };

    private static ShareOutcomeRequest _Share(string activityRef, string completedOn, long revision = 1, string fixedIsk = "1500000",
        LootBasis lootBasis = LootBasis.CargoDifference, int durationSeconds = 900, RunOutcomeKey? key = null, long quantity = 3) =>
        new()
        {
            ActivityRef = activityRef,
            Revision = revision,
            Key = key ?? _SiteKey(),
            CompletedOn = completedOn,
            DurationSeconds = durationSeconds,
            FixedIsk = fixedIsk,
            FleetSize = 1,
            LootBasis = (int)lootBasis,
            Loot =
            {
                new WireLootLine { Kind = (int)RunOutcomeLootKind.Gained, TypeId = 30745, Quantity = quantity },
                new WireLootLine { Kind = (int)RunOutcomeLootKind.Lost, TypeId = 263, Quantity = 1 }
            }
        };

    private static string _Today(int days) =>
        DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<IReadOnlyList<RunOutcome>> _StoredAsync()
    {
        await using ServerDbContext db = await ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContextAsync(Cancellation);
        return await db.Set<RunOutcome>().AsNoTracking().Include(outcome => outcome.LootLines).ToListAsync(Cancellation);
    }

    private async Task<int> _LootLineCountAsync()
    {
        await using ServerDbContext db = await ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContextAsync(Cancellation);
        return await db.Set<StoredLootLine>().CountAsync(Cancellation);
    }

    // Past what a share may carry, so only the store itself can hold it: what retention and the window have to deal with.
    private async Task _SeedAsync(DateOnly completedOn)
    {
        await using ServerDbContext db = await ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContextAsync(Cancellation);
        db.Add(new RunOutcome
        {
            ActivityId = "seeded-" + completedOn.DayNumber, ContributorBucket = "seeded", KeyCode = "site:Site:7720",
            StatsKind = ActivityStatsKind.Site, SiteTypeSource = SiteTypeSource.Site, TypeId = SleeperCache,
            CompletedOn = completedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), FixedIsk = 1, FleetSize = 1, LootBasis = LootBasis.CargoDifference,
            LootLines = [new StoredLootLine { Kind = RunOutcomeLootKind.Gained, TypeId = 30745, Quantity = 1 }]
        });
        await db.SaveChangesAsync(Cancellation);
    }

    private static string[] _Columns(ServerDbContext db, Type entity) =>
        [.. (db.Model.FindEntityType(entity)?.GetProperties() ?? []).Select(property => property.Name).Order(StringComparer.Ordinal)];

    private sealed class HeadersOnlyCallContext(Metadata headers) : ServerCallContext
    {
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override string MethodCore => "test";
        protected override string HostCore => "test";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => throw new NotSupportedException();
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
