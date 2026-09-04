using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Identity;

namespace EveUtils.Client.ViewModels.Fleets;

/// <summary>
/// One lane per character of <b>this</b> client in the band at the top of the fleet overview (ET-170): in which
/// started fleet is this pilot, if any. The band is my pilots and never the fleet's members, so it does not grow
/// with the fleet; and a pilot in no fleet keeps their lane, dimmed, with a START in it — a character that drops
/// off the screen is a character you forget (ET-131's rule).
///
/// The lane carries one primary and one secondary action for the wide card, and every action again in its context
/// menu for the compact line, where there is no room beside the clock.
/// </summary>
public sealed partial class FleetLaneViewModel : ObservableObject
{
    public FleetLaneViewModel(Character character)
    {
        Character = character;
        CharacterName = character.Name;
        CharacterId = character.EsiCharacterId ?? 0;
    }

    public Character Character { get; }
    public string CharacterName { get; }
    public int CharacterId { get; }

    /// <summary>The started fleet this character counts for, or null when they are in none.</summary>
    public FleetViewModel? Fleet { get; init; }

    public bool IsIdle => Fleet is null;

    /// <summary>Holds the fleet-commander seat of <see cref="Fleet"/>.</summary>
    public bool IsFleetCommander { get; init; }

    /// <summary>On the roster of another started fleet as well, where they do not count — the situation the whole
    /// screen exists to make visible. Draws the lane amber.</summary>
    public bool IsElsewhereActive { get; init; }

    /// <summary>How many standing-by fleets the character is rostered in, for the idle lane's chip.</summary>
    public int StandingByCount { get; init; }

    /// <summary>Whether this pilot's EVE client is up, from the local sweep; null when the sweep is not available.</summary>
    public bool? IsInGame { get; init; }

    /// <summary>
    /// The lane is drawn as the narrow card of scherm 10: name, fleet and clock, and nothing else. Set from
    /// <c>FleetOverviewLayout</c> rather than read from a style, because the things it turns off are bound
    /// locally and a local value beats a style setter (the ET-42 trap, here on <c>IsVisible</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleChipText))]
    [NotifyPropertyChangedFor(nameof(ShowRoleChip))]
    [NotifyPropertyChangedFor(nameof(FleetText))]
    [NotifyPropertyChangedFor(nameof(ShowFootChips))]
    private bool _isSlim;

    public string RoleChipText => IsIdle ? "no active fleet"
        : IsElsewhereActive ? (IsSlim ? "ELSEWHERE" : "ELSEWHERE ACTIVE")
        : IsFleetCommander ? "FC" : "member";

    /// <summary>The slim card keeps only what its colour cannot say: FC, and that the pilot counts elsewhere. Being
    /// an ordinary member is what a lane already means, and an idle lane says so on its fleet line instead.</summary>
    public bool ShowRoleChip => !IsSlim || IsFleetCommander || IsElsewhereActive;

    public bool IsRoleOk => !IsIdle && !IsElsewhereActive && IsFleetCommander;
    public bool IsRoleWarn => !IsIdle && IsElsewhereActive;
    public bool IsRoleDim => IsIdle || (!IsElsewhereActive && !IsFleetCommander);

    /// <summary>The slim card has no chip to say there is no fleet, so its fleet line says it in words.</summary>
    public string FleetText => Fleet?.Name ?? (IsSlim ? "no active fleet" : "—");
    public string FleetOriginText => Fleet is null ? "" : $" · {Fleet.OriginText}";

    /// <summary>The third line: whom the pilot flies under, or what the fleet holds when they command it.</summary>
    public string WhereText { get; init; } = "";

    /// <summary>The compact line's middle column: the fleet, or that there is none and how many stand by.</summary>
    public string CompactFleetText => Fleet is not null
        ? Fleet.Name
        : StandingByCount switch
        {
            0 => "no active fleet",
            // Short on purpose: this line shares its column with a fleet name, and the long form was the one that
            // ran into the ellipsis at 758 (scherm 13 writes it just as short).
            var n => string.Create(CultureInfo.InvariantCulture, $"no active fleet · {n} ready"),
        };

    /// <summary>Chips under the clock: when the fleet started, what this pilot shares, where else they stand by.</summary>
    public ObservableCollection<FleetCountChipViewModel> FootChips { get; } = [];

    /// <summary>The slim card drops the chips — there is no width for them, and everything they warn about is said
    /// again on the fleet's own row below (the amber edge, the ELSEWHERE chip, the member's "not linked").</summary>
    public bool ShowFootChips => !IsSlim && FootChips.Count > 0;

    [ObservableProperty] private string _clockText = "--:--:--";

    /// <summary>Time since the fleet started, from the same stamp the row's clock counts from. Invariant (ET-34).</summary>
    public void Tick(DateTimeOffset now)
    {
        if (Fleet?.Info.ActivatedAt is not { } started)
        {
            ClockText = "--:--:--";
            return;
        }

        var elapsed = now - started;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        ClockText = string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    /// <summary>STOP for the commander, LEAVE for a member, START… for an idle pilot with a fleet standing by.</summary>
    public string PrimaryActionText { get; init; } = "";
    public IRelayCommand? PrimaryCommand { get; init; }
    public bool PrimaryIsAccent { get; init; }
    public bool HasPrimaryAction => PrimaryCommand is not null && PrimaryActionText.Length > 0;

    /// <summary>"manage" for the commander, "metrics" for a member.</summary>
    public string SecondaryActionText { get; init; } = "";
    public IRelayCommand? SecondaryCommand { get; init; }
    public bool HasSecondaryAction => SecondaryCommand is not null && SecondaryActionText.Length > 0;

    /// <summary>Every action of the lane, for the right-click menu — the compact form's only route to them.</summary>
    public IReadOnlyList<FleetMemberMenuItemViewModel> MenuItems { get; init; } = [];

    /// <summary>The row this lane points at, so clicking a lane can unfold the same fleet in the table.</summary>
    public IRelayCommand? RevealCommand { get; init; }
}
