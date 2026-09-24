using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Server.Auth;
using EveUtils.Server.DataExplorer;
using EveUtils.Server.Esi;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The Dashboard opens on every visit, and its tiles and attention items each look at several tables. The natural way to
/// write that is one query per tile or per item, which reads fine and is invisible in the page: it renders the same at
/// ten rows and at ten thousand, only slower. So the number of commands the overview sends is pinned to not depend on
/// how much data there is.
/// </summary>
public sealed class DashboardOverviewQueryCountTests
{
    [Fact]
    public async Task Overview_TwentyTimesTheData_SendsTheSameNumberOfCommands()
    {
        var few = await _CommandsForAsync(rows: 2);
        var many = await _CommandsForAsync(rows: 40);

        Assert.True(few > 0);
        Assert.Equal(few, many);
    }

    private static async Task<int> _CommandsForAsync(int rows)
    {
        var ct = TestContext.Current.CancellationToken;
        var counter = new CommandCounter();
        using var factory = new SqliteServerDbContextFactory(counter);
        await _SeedAsync(factory, rows, ct);

        var dataAdmin = new DataAdminService(
            factory, new SharedFitRepository(factory), new ServerAuthRepository(factory), new FleetCompositionRepository(factory), new NoDispatcher(),
            UnusedReleaser.Create(factory));
        var nameLookup = new EsiNameLookup(new UnreachableEsi(), NullLogger<EsiNameLookup>.Instance, TimeProvider.System);
        var overview = new DashboardOverviewService(factory, dataAdmin, nameLookup, TimeProvider.System);

        counter.Reset();
        var result = await overview.GetOverviewAsync(ct);

        // Every kind of attention item that scales with the data must actually be present, or the count proves nothing.
        Assert.True(result.Attention.Count >= rows);
        return counter.Count;
    }

    // Each row seeds a failing character, a session past the idle window, a fleet pointing at a missing composition, a
    // member, fit and run of an unpaired character, and a composition entry linking to a fit that is gone.
    private static async Task _SeedAsync(SqliteServerDbContextFactory factory, int rows, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = ((IDbContextFactory<ServerDbContext>)factory).CreateDbContext();
        for (var i = 1; i <= rows; i++)
        {
            var character = new SyncedCharacter
            {
                EsiCharacterId = 90000000 + i, CharacterName = $"Pilot {i}", PairedAt = now.AddDays(-90), FailureCount = 3, LastFailedAt = now.AddHours(-1),
            };
            db.Add(character);
            db.Add(new ServerSession
            {
                SyncedCharacter = character, AccessTokenHash = "h", RefreshTokenHash = "h", IssuedAt = now.AddDays(-90), ExpiresAt = now.AddDays(-89), LastHeartbeat = now.AddDays(-75),
            });
            db.Add(new Fleet { Name = $"Fleet {i}", CreatorCharacterId = 95000000 + i, CreatedAt = now, LastActivityAt = now.AddDays(-20), FleetCompositionId = 900000 + i });
            db.Add(new SharedFit { EsiFittingId = i, Name = $"Fit {i}", ShipTypeId = 32880, SharedByCharacterId = 95000000 + i, SharedByCharacterName = "Gone", SharedAt = now });
            db.Add(new Run { Id = Guid.NewGuid(), CharacterId = 95000000 + i, GroupCode = $"G{i}", StartedAtUtc = now.UtcDateTime });
            db.Add(new FleetComposition { Name = $"Doctrine {i}", OwnerCharacterId = 90000000 + i, CreatedAt = now, UpdatedAt = now });
        }
        await db.SaveChangesAsync(ct);

        foreach (var composition in await db.Set<FleetComposition>().ToListAsync(ct))
            db.Add(new FleetCompositionRole { CompositionId = composition.Id, RoleName = "Miners", SortOrder = 0 });
        await db.SaveChangesAsync(ct);

        foreach (var role in await db.Set<FleetCompositionRole>().ToListAsync(ct))
            db.Add(new FleetCompositionEntry { RoleId = role.Id, Fit = new FitReference { ShipTypeId = 32880, FitName = "Gone", ServerSharedFitId = 800000 + (int)role.Id } });
        await db.SaveChangesAsync(ct);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;
        public void Reset() => _count = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class UnreachableEsi : IEsiClient
    {
        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ServerError, "ESI is down", 503)));
    }

    private sealed class NoDispatcher : IDispatcher
    {
        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send(ICommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
