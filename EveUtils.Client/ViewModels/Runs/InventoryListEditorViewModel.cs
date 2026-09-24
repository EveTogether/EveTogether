using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The box a list is rewritten in by hand — LOOT's (ET-65, ET-215) and CONSUMABLES' (ET-334) alike: name, tab,
/// quantity, read as it is typed through the clipboard watch's own reading, so the same text cannot be a list in one
/// place and refused in the other. What the list then becomes is the owner's business, handed in as
/// <paramref name="save"/>, which answers with why it was not stored, or null when it was.
/// </summary>
/// <param name="acceptsEmpty">An emptied box is a list too: spending nothing at all is a real answer, where a run
/// with no loot is one to clear by leaving captures out rather than by writing nothing.</param>
public sealed partial class InventoryListEditorViewModel(
    ISdeAccessor? sde, Func<InventoryTextReading, CancellationToken, Task<string?>> save, bool acceptsEmpty = false)
    : ViewModelBase
{
    /// <summary>The box is open. While it is, the box is the list: what is in it is what "done" will make it.</summary>
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private string? _text;

    /// <summary>Why the box cannot be accepted as it stands, in the clipboard watch's own words. Beside the text it
    /// turns down rather than in a toast, and it is the reason "done" is greyed: silently dropping a row the pilot
    /// typed is the one outcome worth blocking on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private string? _refusal;

    /// <summary>The list is being written — set by the owner, which may be writing something else too.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    [NotifyPropertyChangedFor(nameof(FinishText))]
    private bool _isBusy;

    /// <summary>Another write the owner has to wait for first, without this one being under way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private bool _isHeld;

    public bool CanFinish => sde is not null && !IsBusy && !IsHeld && Refusal is null
                             && (acceptsEmpty || !string.IsNullOrWhiteSpace(Text));

    public string FinishText => IsBusy ? "SAVING…" : "SAVE THIS LIST";

    public void Open(string text)
    {
        Text = text;
        Refusal = null;
        IsOpen = true;
    }

    /// <summary>Closes the box and forgets what was in it — the owner calls this itself once the list is stored,
    /// before it reads the result back, so that read is never held for a box still open.</summary>
    public void Close()
    {
        IsOpen = false;
        Text = null;
        Refusal = null;
    }

    [RelayCommand]
    private void Cancel() => Close();

    partial void OnTextChanged(string? value) =>
        Refusal = sde is null || string.IsNullOrWhiteSpace(value) ? null : InventoryTextReading.Read(value, sde).Refusal;

    /// <summary>Hands the list over and closes the box once it is stored. A box that reads as no list at all is
    /// refused here rather than stored as nothing — unless nothing is what was left in it on purpose.</summary>
    public async Task<bool> FinishAsync(CancellationToken cancellationToken = default)
    {
        if (!CanFinish || sde is null)
            return false;

        InventoryTextReading reading = string.IsNullOrWhiteSpace(Text)
            ? new InventoryTextReading([], 0, Refusal: null, IsSingleUnknownRow: false)
            : InventoryTextReading.Read(Text, sde);
        if (reading.Lines.Count == 0 && !string.IsNullOrWhiteSpace(Text))
        {
            Refusal = reading.Refusal ?? "Nothing in this text reads as an EVE inventory listing.";
            return false;
        }

        string? refusal = await save(reading, cancellationToken);
        if (refusal is not null)
        {
            Refusal = refusal;
            return false;
        }

        Close();
        return true;
    }

    /// <summary>The "done" button. Greyed on <see cref="CanFinish"/> rather than refusing after the click.</summary>
    [RelayCommand]
    private Task SaveAsync() => FinishAsync();
}
