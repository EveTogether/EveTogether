using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One figure on a FLEET row — "bounty" and "270,000" — or a quiet word where there is no figure to give.</summary>
public sealed record FleetFigure(string Label, string Value, bool IsQuiet = false);

/// <summary>
/// One character on FLEET (ET-272), in the run window and on the detail screen alike: what this character made in the
/// run — bounty, loot, ore, a homefront's payout — as one line, beside the name and "Local" for this client's own.
///
/// A Local character's figures are its own run's, added up by the ISK registry, and never "not shared": there is
/// nothing to share with oneself. Only a fleet mate's figure can be "not shared", and only when their client said so;
/// a figure nobody has yet is simply not on the line.
///
/// The loot split (ET-105) is an exception, not a column: everybody shares, and taking one character out of it — the
/// hauler — is one click on their row.
/// </summary>
public sealed partial class FleetCharacterRowViewModel(long characterId, Func<FleetCharacterRowViewModel, Task>? onToggleShare = null)
    : ObservableObject
{
    public long CharacterId { get; } = characterId;

    [ObservableProperty] private string _name = $"Char {characterId}";

    [ObservableProperty] private bool _isLocal;

    /// <summary>The quiet line under the name: where a fleet mate said they are, or on a saved activity that this
    /// character's run times were typed by hand. Null for nothing to say.</summary>
    [ObservableProperty] private string? _subText;

    [ObservableProperty] private IReadOnlyList<FleetFigure> _figures = [];

    /// <summary>Whether this character takes a part of the loot split (ET-105) — true for everyone until somebody
    /// takes them out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLeftOutOfSplit))]
    [NotifyPropertyChangedFor(nameof(ShareActionText))]
    private bool _isSharing = true;

    /// <summary>A Local character with a run of its own, in a group of more than one — the only rows a split between
    /// this client's characters can take anyone out of.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleShareCommand))]
    private bool _canToggleShare;

    /// <summary>Zebra striping on the activity detail screen (ET-285), set by the caller once the row's final
    /// position in the (Local-first, then alphabetical) list is known.</summary>
    [ObservableProperty] private bool _isAlternate;

    public bool IsLeftOutOfSplit => !IsSharing;

    public string ShareActionText => IsSharing ? "leave out of loot split" : "put back in loot split";

    [RelayCommand(CanExecute = nameof(CanToggleShare))]
    private async Task ToggleShareAsync()
    {
        if (onToggleShare is not null)
            await onToggleShare(this);
    }

    /// <summary>The figures of a character whose runs are in this activity, read off the registry's own breakdown of
    /// them — the same numbers TOTAL ISK is the sum of.</summary>
    public static IReadOnlyList<FleetFigure> FiguresOf(IskBreakdown isk)
    {
        List<FleetFigure> figures = [];
        _Add(figures, "bounty", isk.Of(IskSource.Bounty));
        _Add(figures, "loot", isk.Of(IskSource.Loot));
        _Add(figures, "reward", isk.Of(IskSource.Rewards));
        _Add(figures, "ore", isk.Of(IskSource.Mining));
        _Add(figures, "payout", isk.Of(IskSource.HomefrontPayout));
        _Add(figures, "costs", isk.Of(IskSource.Consumables));
        _Add(figures, "lost", isk.Of(IskSource.ShipLoss));
        return figures.Count > 0 ? figures : [new FleetFigure("nothing", string.Empty, IsQuiet: true)];
    }

    /// <summary>A fleet mate's figures as their own client shares them over the fleet stream (ET-242) — "not shared"
    /// only for a figure their client said it withholds.</summary>
    public static IReadOnlyList<FleetFigure> FiguresOf(decimal? bounty, decimal? loot, bool isBountyWithheld, bool isLootWithheld)
    {
        List<FleetFigure> figures = [];
        if (bounty is { } shared && shared != 0)
            figures.Add(new FleetFigure("bounty", IskFormat.Number(shared)));
        else if (isBountyWithheld)
            figures.Add(new FleetFigure("bounty not shared", string.Empty, IsQuiet: true));
        if (loot is { } sharedLoot && sharedLoot != 0)
            figures.Add(new FleetFigure("loot", IskFormat.Number(sharedLoot)));
        else if (isLootWithheld)
            figures.Add(new FleetFigure("loot not shared", string.Empty, IsQuiet: true));
        return figures.Count > 0 ? figures : [new FleetFigure("nothing", string.Empty, IsQuiet: true)];
    }

    // A zero is left off rather than written out — "bounty 0" on a hauler's line says nothing a missing figure does
    // not — and a figure nobody can price says so instead of passing for zero.
    private static void _Add(List<FleetFigure> figures, string label, IskContribution? contribution)
    {
        if (contribution is null)
            return;
        if (contribution.Certainty is IskCertainty.Unknown)
            figures.Add(new FleetFigure($"{label} not priced yet", string.Empty, IsQuiet: true));
        else if (contribution.Amount != 0)
            figures.Add(new FleetFigure(label, IskFormat.Number(contribution.Amount)));
    }

    /// <summary>The one line under the rows once somebody is out of the loot split — how much each of the others
    /// takes. Null in the normal case, where everybody shares and there is nothing to say.</summary>
    public static string? LootSplitText(IReadOnlyCollection<FleetCharacterRowViewModel> localRows, decimal? localLootIsk)
    {
        int leftOut = localRows.Count(row => row.IsLeftOutOfSplit);
        if (leftOut == 0)
            return null;

        int sharing = localRows.Count - leftOut;
        return (sharing, localLootIsk) switch
        {
            (0, _) => "Loot split: nobody takes a share.",
            (_, > 0) when localLootIsk is { } loot =>
                $"Loot split: {IskFormat.Whole(loot / sharing)} each · {sharing} sharing, {leftOut} left out",
            _ => $"Loot split: no loot to split yet · {sharing} sharing, {leftOut} left out"
        };
    }
}
