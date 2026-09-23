using System;
using EveUtils.Client.Esi;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Location;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-324: every scope-bound field on a pilot row is checked per character. A field whose scope was not
/// shared must read "not shared" — never a dash, a zero or a stale value that looks like data — and that is exactly the
/// kind of wrong a render can hide (an empty cell and "not shared" differ by one dim word), so it is pinned here. In
/// the real data only the main character shares the ship scope.</summary>
public sealed class HomePilotScopeTests
{
    private const int CharacterId = 96000001;

    private static readonly string[] AllScopes =
    [
        LocationScopeCatalog.ReadLocation, LocationScopeCatalog.ReadShipType, SkillsScopeCatalog.ReadSkillQueue
    ];

    private static HomePilotRowViewModel _Row(bool onThisPc, params string[] scopes)
    {
        var character = new CharacterViewModel(new Character("Noahmarr", CharacterId, scopes)) { HasActiveClient = onThisPc };
        return new HomePilotRowViewModel(character, HomeNavigation.None);
    }

    private static ShipFitDetectionReading _Gila(ShipFitDetectionState state = ShipFitDetectionState.Observed) =>
        new(state, DateTimeOffset.UtcNow, 17715, 1, "Gila", new ShipFitCandidate(1, "Active Exotic", 17715),
            ShipFitMatchReason.ShipName, []);

    private static readonly CharacterSkillQueueEntry[] Queue =
    [
        new() { CharacterId = CharacterId, QueuePosition = 0, SkillTypeId = 3300, FinishedLevel = 5,
            StartDate = DateTimeOffset.UtcNow.AddDays(-1), FinishDate = DateTimeOffset.UtcNow.AddDays(2) }
    ];

    /// <summary>The ship scope not shared: FLYING says so even when the detection holds a reading — a reading it could
    /// only have from elsewhere must not leak onto this character's row. Red with the scope check removed: "Gila".</summary>
    [Fact]
    public void WithoutTheShipScope_FlyingIsNotShared_EvenWithAReading()
    {
        HomePilotRowViewModel row = _Row(onThisPc: true, LocationScopeCatalog.ReadLocation, SkillsScopeCatalog.ReadSkillQueue);

        row.ShowShip(_Gila(), "Gila");

        Assert.Equal(FlyingState.NotShared, row.Flying);
        Assert.Equal(string.Empty, row.HullText);
    }

    [Fact]
    public void WithTheShipScope_FlyingShowsTheHullAndItsFit()
    {
        HomePilotRowViewModel row = _Row(onThisPc: true, AllScopes);

        row.ShowShip(_Gila(), "Gila");

        Assert.Equal(FlyingState.Ship, row.Flying);
        Assert.Equal("Gila", row.HullText);
        Assert.Equal("Active Exotic", row.FitText);
    }

    /// <summary>ESI saying the scope is missing (a token older than the selection) reads the same as not shared.</summary>
    [Fact]
    public void AScopeMissingReading_IsNotShared()
    {
        HomePilotRowViewModel row = _Row(onThisPc: true, AllScopes);

        row.ShowShip(_Gila(ShipFitDetectionState.ScopeMissing), "Gila");

        Assert.Equal(FlyingState.NotShared, row.Flying);
    }

    /// <summary>The skill queue scope not shared: TRAINING says so even if stored rows exist (from before the pilot
    /// withdrew it). Red with the check removed: the stored skill shows.</summary>
    [Fact]
    public void WithoutTheQueueScope_TrainingIsNotShared_EvenWithStoredRows()
    {
        HomePilotRowViewModel row = _Row(onThisPc: true, LocationScopeCatalog.ReadLocation);

        row.ShowQueue(SkillQueueStanding.From(Queue, _ => "Armor Rigging"));

        Assert.Equal(TrainingState.NotShared, row.Training);
    }

    [Fact]
    public void WithTheQueueScope_TrainingShowsTheSkill()
    {
        HomePilotRowViewModel row = _Row(onThisPc: true, AllScopes);

        row.ShowQueue(SkillQueueStanding.From(Queue, _ => "Armor Rigging"));

        Assert.Equal(TrainingState.Training, row.Training);
        Assert.Equal("Armor Rigging V", row.SkillText);
    }

    /// <summary>WHERE, on this PC before the first jump: without the location scope nothing can fill the gap, so it is
    /// "not shared"; with it, "locating…" until ESI or the game log answers. The game log needs no scope, so a system it
    /// names is shown either way.</summary>
    [Fact]
    public void Where_FollowsTheLocationScope_UntilTheGameLogNamesASystem()
    {
        HomePilotRowViewModel without = _Row(onThisPc: true, LocationScopeCatalog.ReadShipType);
        HomePilotRowViewModel with = _Row(onThisPc: true, AllScopes);

        Assert.Equal(WhereState.NotShared, without.Where);
        Assert.Equal(WhereState.Locating, with.Where);

        without.ShowSystem("Hakshma", 0.4);
        Assert.Equal(WhereState.System, without.Where);
        Assert.Equal("Hakshma", without.SystemName);
    }

    /// <summary>No client on this PC is "not on this PC" in WHERE and FLYING — never "offline", and never a scope
    /// complaint: the probe cannot see another machine, and there is nothing to share about a client that is not here.</summary>
    [Fact]
    public void NotOnThisPc_IsNeitherOfflineNorNotShared()
    {
        HomePilotRowViewModel row = _Row(onThisPc: false);

        Assert.Equal(WhereState.NotOnThisPc, row.Where);
        Assert.Equal(FlyingState.NotOnThisPc, row.Flying);
    }
}
