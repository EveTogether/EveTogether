using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One character's block in the LOOT section (ET-215, variant 3): name and portrait, what their own run's loot comes
/// to, the items it is made of, and — folded away until asked for — every capture behind that subtotal. One block per
/// run rather than per character id: a correction is always to one run's captures, and the hand-written list replaces
/// one run's loot, so a block that spanned two runs would have nothing single to rewrite.
///
/// The loot itself is <see cref="RunLootViewModel"/>'s, unchanged: the same valuation, the same tally, the same
/// exclusion and hand-written list the run window always had. This block only decides how it is laid out.
/// </summary>
public sealed partial class ActivityLootCharacterViewModel : ObservableObject
{
    public ActivityLootCharacterViewModel(Guid runId, long characterId, string characterName, RunLootViewModel loot)
    {
        RunId = runId;
        CharacterId = characterId;
        CharacterText = characterName;
        Loot = loot;
        Loot.RunId = runId;
        Loot.PropertyChanged += _OnLootChanged;
        Loot.Captures.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DisclosureText));
    }

    public Guid RunId { get; }

    public long CharacterId { get; }

    public string CharacterText { get; }

    public RunLootViewModel Loot { get; }

    public string Initial => string.IsNullOrEmpty(CharacterText) ? "?" : CharacterText[..1].ToUpperInvariant();

    /// <summary>The pilot's ESI portrait, from the same provider the runs screen's lanes and the character column use
    /// — null until it lands, or for good when images are off, and the hex shows the initial instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortrait))]
    private Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;

    /// <summary>Folded by default: the block is there to read "who brought in what" at a glance, and every character
    /// open at once would bury exactly that (the mockup's own decision, matching the run window's strip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisclosureText))]
    [NotifyPropertyChangedFor(nameof(DisclosureChevron))]
    private bool _isCapturesShown;

    /// <summary>Says what clicking it will do next, and how much is behind it.</summary>
    public string DisclosureText
    {
        get
        {
            int count = Loot.Captures.Count;
            return $"{(IsCapturesShown ? "Showing" : "Show")} {count} {(count == 1 ? "capture" : "captures")}";
        }
    }

    /// <summary>The quieter half of the disclosure, in dimmer ink beside it (ET-215 mockup).</summary>
    public string DisclosureExcludedText => $"({Loot.ExcludedCount} excluded)";

    public string DisclosureChevron => IsCapturesShown ? "▾" : "▸";

    public string SubtotalText => Loot.NetIskDisplay;

    /// <summary>Why the block has nothing under its name, rather than an empty table that looks like a failed load. A
    /// run from before loot was filed per character (ET-211) carries all of it on one of the group's runs, and the
    /// others simply have none — shown as none, never shared out after the fact.</summary>
    public string EmptyText => Loot.IsReadOnly
        ? "No loot was copied on this character's run."
        : "No loot was copied on this character's run. Rewrite it by hand to give it some.";

    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits)
    {
        if (CharacterId is > 0 and <= int.MaxValue)
            Portrait = await portraits.GetPortraitAsync((int)CharacterId, 64);
    }

    private void _OnLootChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunLootViewModel.NetIsk))
            OnPropertyChanged(nameof(SubtotalText));
        else if (e.PropertyName is nameof(RunLootViewModel.ExcludedCount))
            OnPropertyChanged(nameof(DisclosureExcludedText));
        else if (e.PropertyName is nameof(RunLootViewModel.IsReadOnly))
            OnPropertyChanged(nameof(EmptyText));
    }
}
