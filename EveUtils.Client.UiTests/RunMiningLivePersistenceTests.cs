using Avalonia.Headless.XUnit;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-229: mining used to go nowhere but the lifetime <c>CharacterMetrics</c> total — nothing landed on the run
/// itself, the same gap ET-219 closed for bounty. These are the mining counterparts, following the identical
/// live-capture pattern (<see cref="AddRunMiningEntryCommand"/>).
/// </summary>
public sealed class RunMiningLivePersistenceTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Counter-proof, red before this fix: mining never reached a <c>RunMiningEntry</c> row at all. Two mining
    /// cycles of the same ore aggregate onto one row (units, not one row per cycle) — a site is on the order of a
    /// hundred cycles per character.
    /// </summary>
    [AvaloniaFact]
    public async Task MiningCycles_AggregatePerOre_OnTheRunImmediately()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Assert.True(started.IsSuccess);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "Pilot");
        await gamelog.AddMiningAsync("Pilot",
            new MiningEvent(StartedAtUtc.AddMinutes(1), 13, "Amperum Mutanite", IsCritical: false, LostResidue: 0));
        await gamelog.AddMiningAsync("Pilot",
            new MiningEvent(StartedAtUtc.AddMinutes(2), 14, "Amperum Mutanite", IsCritical: true, LostResidue: 0));
        // The residue-only correction (Units=0) the correlator synthesises for the ore-less residue line.
        DateTime residueAtUtc = StartedAtUtc.AddMinutes(2).AddSeconds(5);
        await gamelog.AddMiningAsync("Pilot",
            new MiningEvent(residueAtUtc, 0, "Amperum Mutanite", IsCritical: false, LostResidue: 6));

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunMiningEntry entry = Assert.Single(await db.Set<RunMiningEntry>()
            .Where(e => e.RunId == started.Value).ToListAsync(cancellationToken));
        Assert.Equal("Amperum Mutanite", entry.OreType);
        Assert.Equal(27, entry.Units);
        Assert.Equal(14, entry.CriticalUnits);
        Assert.Equal(6, entry.ResidueUnits);
        Assert.Equal(StartedAtUtc.AddMinutes(1), entry.FirstObservedAtUtc);
        Assert.Equal(residueAtUtc, entry.LastObservedAtUtc);
    }

    /// <summary>Two different ores on the same run get two rows, each aggregated on its own.</summary>
    [AvaloniaFact]
    public async Task DifferentOres_EachGetTheirOwnRow()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "Pilot");
        await gamelog.AddMiningAsync("Pilot",
            new MiningEvent(StartedAtUtc.AddMinutes(1), 13, "Amperum Mutanite", IsCritical: false, LostResidue: 0));
        await gamelog.AddMiningAsync("Pilot",
            new MiningEvent(StartedAtUtc.AddMinutes(2), 624, "Raspite X-Grade", IsCritical: false, LostResidue: 0));

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        List<RunMiningEntry> entries = await db.Set<RunMiningEntry>()
            .Where(e => e.RunId == started.Value).ToListAsync(cancellationToken);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.OreType == "Amperum Mutanite" && e.Units == 13);
        Assert.Contains(entries, e => e.OreType == "Raspite X-Grade" && e.Units == 624);
    }

    /// <summary>Two of the same pilot's characters, each with their own running run — mining lands only on the
    /// character's own run, never the other's, the same isolation ET-219's AC-5 proves for bounty.</summary>
    [AvaloniaFact]
    public async Task TwoCharactersEachRunning_EachLiveMiningLandsOnItsOwnRunOnly()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> firstRun = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Result<Guid> secondRun = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000143), cancellationToken);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "First Pilot");
        gamelog.MapCharacter(90000002, "Second Pilot");
        await gamelog.AddMiningAsync("First Pilot",
            new MiningEvent(StartedAtUtc.AddMinutes(1), 40, "Amperum Mutanite", IsCritical: false, LostResidue: 0));
        await gamelog.AddMiningAsync("Second Pilot",
            new MiningEvent(StartedAtUtc.AddMinutes(1), 90, "Amperum Mutanite", IsCritical: false, LostResidue: 0));

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunMiningEntry firstEntry = Assert.Single(await db.Set<RunMiningEntry>()
            .Where(e => e.RunId == firstRun.Value).ToListAsync(cancellationToken));
        RunMiningEntry secondEntry = Assert.Single(await db.Set<RunMiningEntry>()
            .Where(e => e.RunId == secondRun.Value).ToListAsync(cancellationToken));
        Assert.Equal(40, firstEntry.Units);
        Assert.Equal(90, secondEntry.Units);
    }
}
