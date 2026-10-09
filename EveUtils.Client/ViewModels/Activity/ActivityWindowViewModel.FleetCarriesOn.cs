using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// A pilot out of a fleet abyssal while others are still inside (ET-498) — dead, or out by any other way. Their own
/// gamelog sees nothing of the pocket any more, so the window follows the run on a mate who is still in it: that mate's
/// rooms and enemies in ENEMIES and on the ROOM line, read off the mate's <see cref="RunShareUpdate"/>, and a line
/// naming who is still inside. Shown only: SAVE stores the pilot's own rooms and enemies, never the mate's, whose own
/// run carries them.
/// </summary>
public sealed partial class ActivityWindowViewModel
{
    /// <summary>"You are out — the fleet carries on" with who is still inside, or null while this pilot is not out of a
    /// fleet run others are still in.</summary>
    public string? FleetCarriesOnText { get; private set; }

    public bool IsFleetCarryingOn => FleetCarriesOnText is not null;

    private void _RefreshFleetCarriesOn()
    {
        IReadOnlyList<ActivityFleetMemberViewModel> inside = _IsOutOfFleetRun() ? _MatesStillInside() : [];
        string? text = inside.Count == 0
            ? null
            : $"You are out — the fleet carries on. Still inside: {string.Join(", ", inside.Select(mate => mate.Name))}.";

        (string Name, RunShareUpdate Share)? followed = _MateToFollow(inside);
        _Enemies()?.ShowFleetMate(followed?.Name, followed?.Share);

        if (text == FleetCarriesOnText)
            return;

        FleetCarriesOnText = text;
        OnPropertyChanged(nameof(FleetCarriesOnText));
        OnPropertyChanged(nameof(IsFleetCarryingOn));
    }

    // Out of the pocket on this pilot's own leg: a pilot who pressed STOP while still inside is not "out".
    private bool _IsOutOfFleetRun() =>
        RunType.ClockPerPilot && FleetId is not null && GroupCode is not null
        && RunState is ActivityRunState.Stopped && InsideAbyssal is not true;

    // Inside is what each mate's own location sample says: an abyssal anchor rides along only while they are in. This
    // client's own characters in the fleet are no mates: their rooms are their own collectors' already.
    private IReadOnlyList<ActivityFleetMemberViewModel> _MatesStillInside()
    {
        HashSet<int> own = [.. (_services.GetService<IFleetParticipation>()?.Current ?? []).Select(participant => participant.CharacterId)];
        return
        [
            .. FleetMembers.Where(row => !own.Contains(row.CharacterId)
                                         && _fleetLocations.GetValueOrDefault(row.CharacterId) is { AbyssalAnchorMs: > 0 })
        ];
    }

    // The mate furthest into the run — most rooms, then the newest word — is the one whose view is followed.
    private (string Name, RunShareUpdate Share)? _MateToFollow(IReadOnlyList<ActivityFleetMemberViewModel> inside)
    {
        if (GroupCode is not { } groupCode || _fleetShares is null)
            return null;

        (string Name, RunShareUpdate Share)? followed = null;
        foreach (ActivityFleetMemberViewModel mate in inside)
        {
            if (_fleetShares.Of(groupCode, mate.CharacterId) is not { } share || share.FleetId != FleetId
                || share.RoomStartsUnixMs.Count == 0 && share.Enemies.Count == 0)
                continue;

            if (followed is not { } held || share.RoomStartsUnixMs.Count > held.Share.RoomStartsUnixMs.Count
                || share.RoomStartsUnixMs.Count == held.Share.RoomStartsUnixMs.Count && share.UnixMs > held.Share.UnixMs)
                followed = (mate.Name, share);
        }

        return followed;
    }
}
