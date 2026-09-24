using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Esi;
using EveUtils.Client.Killmails;
using EveUtils.Server.Tests;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-383 (epic ET-379): the client's half of the write-repository guard. Nothing in the client takes a write
/// repository in its constructor — a write there would change the local database without the signal the screens listen
/// for — unless it is on the list below with the reason. The shared and server code is held in
/// <c>EveUtils.Server.Tests</c>.
/// </summary>
public sealed class WriteRepositoryGuardTests
{
    /// <summary>Types that write past the command handlers on purpose. An entry here is a claim a reviewer has to agree
    /// with, not a way to make this test pass.</summary>
    private static readonly IReadOnlyDictionary<Type, string> Allowed = new Dictionary<Type, string>
    {
        // ET-381: an ESI sync applier. It mirrors the live in-game fleet onto the planned one on a poll and publishes
        // FleetChangedEvent (RosterChanged) itself on every change it applies.
        [typeof(EsiFleetSyncService)] = "an ESI sync applier: mirrors the live in-game fleet onto the plan on each poll "
            + "and publishes FleetChangedEvent itself for every change it applies",
        // ET-381: copies the MOTD and free-move it just set in game through ESI onto a client-only fleet, and publishes
        // FleetChangedEvent itself afterwards.
        [typeof(FleetEsiControlService)] = "an ESI sync applier: copies the MOTD and free-move it just set in game onto a "
            + "client-only fleet and publishes FleetChangedEvent itself",
        // A cache of character, corporation and alliance names looked up from ESI for killmail screens; it changes no
        // killmail, and the screen that asked for the names reads them straight back.
        [typeof(KillmailNames)] = "a cache of names looked up from ESI for the screen that asked; it changes no killmail"
    };

    [Fact]
    public void NoClientTypeOutsideTheCommandHandlers_TakesAWriteRepository_WithoutAReason()
    {
        IReadOnlyDictionary<Type, Type[]> takers = WriteRepositoryGuard.TakersOutsideCommandHandlers(typeof(EsiKillmailImporter).Assembly);

        string[] unexplained = [.. takers
            .Where(taker => !Allowed.ContainsKey(taker.Key))
            .Select(taker => $"{taker.Key.Name} ({string.Join(", ", taker.Value.Select(repository => repository.Name))})")];
        Assert.True(unexplained.Length == 0,
            $"These take a write repository outside a command handler: {string.Join("; ", unexplained)}. Write through a "
            + "command, so its signal is published, and take the repository's reader half for the reads.");
        Assert.All(Allowed, allowed => Assert.True(takers.ContainsKey(allowed.Key),
            $"{allowed.Key.Name} no longer takes a write repository; take it off the list."));
        Assert.All(Allowed, allowed => Assert.False(string.IsNullOrWhiteSpace(allowed.Value)));
    }
}
