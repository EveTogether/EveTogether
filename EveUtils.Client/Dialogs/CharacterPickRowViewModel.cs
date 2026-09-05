using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Wraps a <see cref="CharacterPickOption"/> with the hex-portrait and selection state
/// <see cref="EveUtils.Client.Views.CharacterPickerWindow"/> needs for its rows. Kept separate from the option
/// record itself (ET-184): every existing <c>PickCharacterAsync</c>/<c>PickCharactersAsync</c> caller keeps handing
/// over a plain <see cref="CharacterPickOption"/>, and the window fetches its own portrait from the
/// <see cref="CharacterId"/> it already has, exactly like a fleet roster leaf does.
/// </summary>
public sealed partial class CharacterPickRowViewModel(CharacterPickOption option) : ObservableObject
{
    public int CharacterId { get; } = option.CharacterId;
    public string Name { get; } = option.Name;
    public string Detail { get; } = option.Detail;
    public bool Enabled { get; } = option.Enabled;

    /// <summary>Mirrors the list's own selection (set by the window on <c>SelectionChanged</c>) so the row's
    /// checkbox/radio mark and card styling can bind to it directly, instead of reaching into the ListBoxItem
    /// container from inside its own data template.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The character's ESI portrait for the row's hex; null until loaded or when images are off/offline,
    /// so the hex falls back to the initial glyph.</summary>
    [ObservableProperty] private Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;
    partial void OnPortraitChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPortrait));

    /// <summary>First letter of the name, shown in the hex when no portrait render is available.</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>Loads the ESI portrait best-effort (opt-in image setting); a failure leaves the glyph fallback.</summary>
    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits, CancellationToken cancellationToken = default)
    {
        if (CharacterId <= 0)
            return;
        Portrait = await portraits.GetPortraitAsync(CharacterId, 64, cancellationToken);
    }
}
