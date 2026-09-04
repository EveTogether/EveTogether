using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Identity;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One of my characters in the run window's column (ET-164): the hex you switch runs with, and the row that says
/// whether that character is flying, resting or asking for something.
///
/// The hex has three fills and not two. "No ESI link" is a state of its own — the pilot has no character id, so
/// there is no portrait to fetch with images on or off — and it gets the hatched box. "Linked but no render"
/// (images off, offline, a 404) keeps the letter glyph. Collapsing both onto <c>Portrait is null</c> is what made
/// all three look alike, and an unlinked member is not the exception: the first fleet Raymond showed had one.
/// </summary>
public sealed partial class RunCharacterRowViewModel : ObservableObject
{
    /// <summary>The same size the main window's character column asks for, so this column shares that cache key
    /// (<c>{id}_128.png</c>) and costs no download of its own. A smaller render would be a second key.</summary>
    public const int PortraitSize = 128;

    public RunCharacterRowViewModel(Character character)
    {
        Name = character.Name;
        CharacterId = character.EsiCharacterId ?? 0;
        Initials = _InitialsOf(character.Name);
    }

    public int CharacterId { get; }

    public string Name { get; }

    /// <summary>Two letters, not one: six toons collide on a first letter long before they collide on two.</summary>
    public string Initials { get; }

    /// <summary>Whether this character has an ESI character id at all — the only thing that decides between the
    /// hatched box and the glyph, and independent of whether images are switched on.</summary>
    public bool IsEsiLinked => CharacterId > 0;

    /// <summary>The ESI render, loaded best-effort; null when images are off, offline, or the fetch failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPortrait))]
    [NotifyPropertyChangedFor(nameof(ShowsGlyph))]
    private Bitmap? _portrait;

    public bool ShowsPortrait => IsEsiLinked && Portrait is not null;

    public bool ShowsGlyph => IsEsiLinked && Portrait is null;

    public bool ShowsUnlinked => !IsEsiLinked;

    /// <summary>What this character is asking for, carried by the ring colour and by the dot together.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWarning))]
    [NotifyPropertyChangedFor(nameof(IsCritical))]
    [NotifyPropertyChangedFor(nameof(HasAttentionDot))]
    private RunCharacterAttention _attention;

    public bool IsWarning => Attention is RunCharacterAttention.Warning;

    public bool IsCritical => Attention is RunCharacterAttention.Critical;

    /// <summary>The dot on the hex's flat right side. It carries the same signal as the ring rather than a second
    /// one: a light portrait eats a coloured ring, and a 7px dot on its own is not findable across the screen.</summary>
    public bool HasAttentionDot => Attention is not RunCharacterAttention.None;

    /// <summary>This character has a run on the clock. A character without one stays in the column and dims —
    /// ring and portrait together, so it reads as resting and not as a state of its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResting))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _hasRunningRun;

    public bool IsResting => !HasRunningRun;

    /// <summary>The run this window is showing.</summary>
    [ObservableProperty] private bool _isSelected;

    public string Tooltip => (IsEsiLinked, HasRunningRun) switch
    {
        (false, _) => $"{Name} — not linked to ESI, so there is no portrait for this pilot",
        (true, false) => $"{Name} — no run on the clock; START files one under this character",
        (true, true) => Name
    };

    /// <summary>Fetches the ESI render best-effort. An unlinked character is skipped outright: it has no id to ask
    /// with, and calling anyway would put the two cases back on the same footing.</summary>
    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits,
        CancellationToken cancellationToken = default)
    {
        if (!IsEsiLinked)
            return;

        Portrait = await portraits.GetPortraitAsync(CharacterId, PortraitSize, cancellationToken);
    }

    private static string _InitialsOf(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries) switch
        {
            [] => "?",
            [{ } single] => (single.Length >= 2 ? single[..2] : single).ToUpperInvariant(),
            [{ } first, { } second, ..] => $"{first[0]}{second[0]}".ToUpperInvariant()
        };
}
