using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>FLEET on the detail screen: every run behind the activity, and how many flew it.</summary>
public sealed partial class FleetDetailSectionViewModel(Func<long, string>? nameOf)
    : RunDetailSection(RunSectionId.Fleet, "FLEET")
{
    public ObservableCollection<ActivityRunRowViewModel> RunRows { get; } = [];

    [ObservableProperty] private string _participantCountText = string.Empty;

    /// <summary>Null once every run in this activity carries a recorded name (ET-212 AC-2) — an activity saved
    /// entirely after that column existed needs no caveat, because the names on screen are then read from storage
    /// rather than reconstructed from whoever happens to still be logged in.</summary>
    [ObservableProperty] private string? _fleetBasisText;

    public override bool HasContent => RunRows.Count > 0;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        RunRows.Clear();
        foreach (ActivityRunDetailDto run in detail.Runs)
            RunRows.Add(new ActivityRunRowViewModel(run, nameOf));

        ParticipantCountText = $"{detail.ParticipantCount} participants";
        // Only once every run here is missing its own recorded name (ET-212) does the caveat still apply — an
        // activity saved entirely after CharacterNameSnapshot existed has nothing left to explain away. A mixed
        // activity (an old run beside a new one, or one synced from a fleetmate's older client) still gets the
        // caveat: some of the names on screen below are still a live lookup or a bare id, not a stored fact.
        FleetBasisText = detail.Runs.Count > 0 && detail.Runs.All(run => !string.IsNullOrEmpty(run.CharacterNameSnapshot))
            ? null
            : "Participant names are not recorded yet, so these are the runs behind this activity by " +
              "character id. The count above is real: it comes from the activity's own distinct characters.";
        HeaderSummary = $"{ParticipantCountText} · {detail.PayoutEligibleCount} sharing";
    }
}
