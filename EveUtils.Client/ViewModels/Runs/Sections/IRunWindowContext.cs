using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// What a run window section may see of the window it stands in (ET-236): the run it is on, where that run is, who is
/// in it and what it has collected so far. The window implements it; a section never reaches past it into the window
/// itself, so a section module can be read, and replaced, on its own.
///
/// <see cref="INotifyPropertyChanged"/> carries changes under the names below — the one way a section learns the run
/// moved on without the window having to know which sections care.
/// </summary>
public interface IRunWindowContext : INotifyPropertyChanged
{
    IServiceProvider Services { get; }

    /// <summary>The kind the window was opened as. For what is remembered per kind, never to decide what to show —
    /// <see cref="RunType"/> is that.</summary>
    ActivityKind Kind { get; }

    RunTypeDefinition RunType { get; }

    ActivityRunState RunState { get; }

    /// <summary>The run on screen — whichever of the group's runs the character column shows.</summary>
    Guid? RunId { get; }

    /// <summary>The character whose run is on screen. Announces no change of its own: the window switches it only as
    /// part of a switch that refreshes everything after it.</summary>
    int? RunCharacterId { get; }

    /// <summary>The run's character, or before START the one character this client is flying in the fleet — null
    /// while that is still a question.</summary>
    int? ActingCharacterId { get; }

    // ── The site ───────────────────────────────────────────────────────────────────────────────────

    string? SignatureId { get; }

    string? SignatureGroup { get; }

    string? SignatureName { get; }

    IReadOnlyList<SdeSite> MatchedSites { get; }

    // ── Where ──────────────────────────────────────────────────────────────────────────────────────

    string? SolarSystem { get; }

    string? LocationDisplay { get; }

    bool IsInsideAbyssal { get; }

    // ── The pocket ─────────────────────────────────────────────────────────────────────────────────
    // Held by the window because its header shows them; set by the section that asks for them.

    int? WeatherIndex { get; set; }

    int? TierIndex { get; set; }

    AbyssalWeather? Weather { get; }

    bool HasWeatherAndTier { get; }

    string TierText { get; }

    // ── Who, and what they made ────────────────────────────────────────────────────────────────────

    long BountyIsk { get; }

    bool IsFleetShown { get; }

    int FleetMemberCount { get; }

    string FleetStatusText { get; }

    /// <summary>Who the window has heard from. A change to the rows is announced as a change of this property.</summary>
    ObservableCollection<ActivityFleetMemberViewModel> FleetMembers { get; }

    ObservableCollection<RunParticipantViewModel> Participants { get; }

    RunLootViewModel? RunLoot { get; }

    ActivityLootViewModel? LootOverview { get; }

    /// <summary>Work the whole window out again, for a section that changed something the window shows.</summary>
    void Refresh(DateTime nowUtc);
}
