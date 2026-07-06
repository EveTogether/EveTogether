using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Esi;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A creator credit on the About page: the EVE character behind a GitHub account, shown with the same hex ESI
/// portrait (and glyph fallback) the character list uses. Both the name and the portrait are resolved from the
/// character id through ESI, so they always describe the same character — a wrong/typo'd id can't silently pair one
/// person's name with another's face. The handle passed in is only a fallback label until ESI resolves (or when
/// offline / images disabled).
/// </summary>
public partial class CreatorRowViewModel : ObservableObject
{
    public int CharacterId { get; }
    public string Role { get; }
    public string GithubUrl { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    partial void OnPortraitChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPortrait));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Initial));

    public CreatorRowViewModel(string fallbackName, string role, string githubUrl, int characterId)
    {
        _name = fallbackName;
        Role = role;
        GithubUrl = githubUrl;
        CharacterId = characterId;
    }

    /// <summary>Resolves the character's real name and portrait from its id. Best-effort: a failed name resolve keeps
    /// the fallback handle; a failed portrait leaves the initial glyph.</summary>
    public async Task LoadAsync(ICharacterPortraitProvider portraits, ICharacterInfoService? characterInfo, CancellationToken cancellationToken = default)
    {
        if (characterInfo is not null)
        {
            var resolved = await characterInfo.GetNameAsync(CharacterId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
                Name = resolved;
        }

        Portrait = await portraits.GetPortraitAsync(CharacterId, 128, cancellationToken);
    }
}
