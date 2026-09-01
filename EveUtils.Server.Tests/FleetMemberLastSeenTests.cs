using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-70's server half. A client that has been shut down never sends the message saying it is gone, so the only
/// evidence is its traffic stopping — and the only place that can see one pilot's traffic stop, while the rest of the
/// fleet plays on, is the server. <see cref="FleetActivityTracker"/> already does this for the fleet as a whole and
/// cannot answer it: one member still publishing keeps the whole fleet's clock fresh.
///
/// Backed by a real <see cref="FleetRepository"/> over throwaway SQLite, because the point of the timestamp is that
/// it survives in the database for a screen that opens later.
/// </summary>
public class FleetMemberLastSeenTests
{
    private const int Flying = 100;
    private const int Departed = 200;

    private readonly SqliteServerDbContextFactory _factory = new();

    private async Task<(FleetRepository Repo, long FleetId)> FleetAsync(CancellationToken ct)
    {
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(
            new FleetEntity { Name = "Home Defense", CreatorCharacterId = Flying, State = FleetState.Active }, ct);

        foreach (var characterId in new[] { Flying, Departed })
            await repo.AddMemberAsync(new FleetMember
            {
                FleetId = fleetId, CharacterId = characterId, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1,
            }, ct);

        return (repo, fleetId);
    }

    private static FleetMemberActivityTracker TrackerOver(IFleetRepository repository) =>
        new(new ServiceCollection().AddScoped(_ => repository).BuildServiceProvider());

    /// <summary>A member starts having never been heard from, which is not the same as having gone quiet.</summary>
    [Fact]
    public async Task ANewMember_HasNeverBeenSeen()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(ct);

        Assert.All(await repo.ListMembersAsync(fleetId, ct), m => Assert.Null(m.LastSeenAt));
    }

    [Fact]
    public async Task Noting_StampsTheMember_SoALaterReaderCanMeasureTheSilence()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(ct);
        var now = DateTimeOffset.UtcNow;

        await TrackerOver(repo).NoteAsync(fleetId, Flying, now, ct);

        var member = Assert.Single(await repo.ListMembersAsync(fleetId, ct), m => m.CharacterId == Flying);
        Assert.Equal(now, member.LastSeenAt!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The whole reason this is per member and not per fleet: the pilot who is still flying keeps the fleet's own
    /// clock fresh, and would have kept everybody else's with it.
    /// </summary>
    [Fact]
    public async Task OneMemberPublishing_DoesNotFreshenAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(ct);

        await TrackerOver(repo).NoteAsync(fleetId, Flying, DateTimeOffset.UtcNow, ct);

        var members = await repo.ListMembersAsync(fleetId, ct);
        Assert.NotNull(Assert.Single(members, m => m.CharacterId == Flying).LastSeenAt);
        Assert.Null(Assert.Single(members, m => m.CharacterId == Departed).LastSeenAt);
    }

    /// <summary>
    /// Samples arrive at 1 Hz per member, so the write is throttled — but to half the silence window, not to the
    /// minute <see cref="FleetActivityTracker"/> uses. A stored timestamp is what a screen reads before it has heard
    /// a sample of its own, so it may never be stale enough to look like silence by itself.
    /// </summary>
    [Fact]
    public async Task Noting_IsThrottled_ButAlwaysWellInsideTheSilenceWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(ct);
        var tracker = TrackerOver(repo);
        var start = DateTimeOffset.UtcNow;

        await tracker.NoteAsync(fleetId, Flying, start, ct);
        // A second later — the very next sample — must not cost another write.
        await tracker.NoteAsync(fleetId, Flying, start + TimeSpan.FromSeconds(1), ct);
        Assert.Equal(start, Seen(await repo.ListMembersAsync(fleetId, ct)), TimeSpan.FromSeconds(1));

        // Past the throttle it is written again, and the gap it just tolerated is inside the silence window with
        // room to spare — so a member publishing all along can never read as having gone quiet.
        var later = start + FleetMemberPresence.SeenWriteThrottle + TimeSpan.FromSeconds(1);
        await tracker.NoteAsync(fleetId, Flying, later, ct);
        Assert.Equal(later, Seen(await repo.ListMembersAsync(fleetId, ct)), TimeSpan.FromSeconds(1));
        Assert.True(later - start < FleetMemberPresence.SilentAfter);

        DateTimeOffset Seen(System.Collections.Generic.IReadOnlyList<FleetMember> members) =>
            Assert.Single(members, m => m.CharacterId == Flying).LastSeenAt!.Value;
    }

    /// <summary>A character who is not on this fleet's roster stamps nothing and throws nothing — the stream carries
    /// whatever the attached session is, and a pilot removed mid-op is exactly that.</summary>
    [Fact]
    public async Task NotingAnOutsider_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(ct);

        await TrackerOver(repo).NoteAsync(fleetId, characterId: 999, DateTimeOffset.UtcNow, ct);

        Assert.All(await repo.ListMembersAsync(fleetId, ct), m => Assert.Null(m.LastSeenAt));
    }

    /// <summary>An unauthenticated stream has no character to attribute anything to; it may not stamp member 0.</summary>
    [Fact]
    public async Task NotingWithNoAttachedCharacter_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(ct);

        await TrackerOver(repo).NoteAsync(fleetId, characterId: 0, DateTimeOffset.UtcNow, ct);

        Assert.All(await repo.ListMembersAsync(fleetId, ct), m => Assert.Null(m.LastSeenAt));
    }
}
