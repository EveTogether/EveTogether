using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Server.Auth;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The panel shows a viewer the destructive actions as disabled buttons without a handler, but a Blazor circuit is a
/// channel a client can write to, and a page is not the only code that will ever call these methods. So the service
/// itself refuses without Data · Delete, and this pins that the refusal is there for every action and leaves the data
/// untouched — something no rendered page can show.
/// </summary>
public sealed class DataAdminDeletePermissionTests
{
    [Fact]
    public async Task EveryDelete_ViewerWithoutDataDelete_IsRefusedAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new SqliteServerDbContextFactory();
        var ids = await _SeedAsync(factory, ct);
        var service = new DataAdminService(
            factory, new SharedFitRepository(factory), new ServerAuthRepository(factory), new FleetCompositionRepository(factory), new NoDispatcher());
        var viewer = _Principal(PanelPermissions.DataView);

        List<Result> results =
        [
            await service.DeleteSharedFitAsync(viewer, ids.FitId, ct),
            await service.DisbandFleetAsync(viewer, ids.FleetId, ct),
            await service.PurgeFleetAsync(viewer, ids.FleetId, ct),
            await service.DeleteFleetCompositionAsync(viewer, ids.CompositionId, ct),
            await service.DeleteSyncedCharacterAsync(viewer, ids.CharacterId, ct),
            await service.DeleteSessionAsync(viewer, ids.SessionId, ct),
        ];

        Assert.All(results, r =>
        {
            Assert.False(r.IsSuccess);
            Assert.Equal(MessageCodes.PermissionDenied, Assert.Single(r.Messages).Code);
        });
        await using var db = ((IDbContextFactory<ServerDbContext>)factory).CreateDbContext();
        Assert.Equal(1, await db.Set<SharedFit>().CountAsync(ct));
        Assert.Equal(FleetState.Active, (await db.Set<Fleet>().SingleAsync(ct)).State);
        Assert.Equal(1, await db.Set<FleetComposition>().CountAsync(ct));
        Assert.Equal(1, await db.Set<SyncedCharacter>().CountAsync(ct));
        Assert.Equal(1, await db.Set<ServerSession>().CountAsync(ct));
    }

    [Fact]
    public async Task DeleteSession_AdminWithDataDelete_RemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new SqliteServerDbContextFactory();
        var ids = await _SeedAsync(factory, ct);
        var service = new DataAdminService(
            factory, new SharedFitRepository(factory), new ServerAuthRepository(factory), new FleetCompositionRepository(factory), new NoDispatcher());

        var result = await service.DeleteSessionAsync(_Principal(PanelPermissions.DataDelete), ids.SessionId, ct);

        Assert.True(result.IsSuccess);
        await using var db = ((IDbContextFactory<ServerDbContext>)factory).CreateDbContext();
        Assert.Equal(0, await db.Set<ServerSession>().CountAsync(ct));
    }

    private sealed record SeededIds(int FitId, long FleetId, long CompositionId, int CharacterId, int SessionId);

    private static ClaimsPrincipal _Principal(string permission) =>
        new(new ClaimsIdentity([new Claim(AdminClaims.Permission, permission)], "test"));

    private static async Task<SeededIds> _SeedAsync(SqliteServerDbContextFactory factory, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = ((IDbContextFactory<ServerDbContext>)factory).CreateDbContext();
        var character = new SyncedCharacter { EsiCharacterId = 90000001, CharacterName = "Pilot", PairedAt = now };
        var session = new ServerSession
        {
            SyncedCharacter = character, AccessTokenHash = "h", RefreshTokenHash = "h", IssuedAt = now, ExpiresAt = now.AddHours(1), LastHeartbeat = now,
        };
        var fleet = new Fleet { Name = "Fleet", CreatorCharacterId = 90000001, CreatedAt = now, LastActivityAt = now };
        var fit = new SharedFit { EsiFittingId = 1, Name = "Fit", ShipTypeId = 32880, SharedByCharacterId = 90000001, SharedByCharacterName = "Pilot", SharedAt = now };
        var composition = new FleetComposition { Name = "Doctrine", OwnerCharacterId = 90000001, CreatedAt = now, UpdatedAt = now };
        db.AddRange(character, session, fleet, fit, composition);
        await db.SaveChangesAsync(ct);
        return new SeededIds(fit.Id, fleet.Id, composition.Id, character.Id, session.Id);
    }

    private sealed class NoDispatcher : IDispatcher
    {
        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send(ICommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
