using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Controls;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// A pilot's face on the runs screen (ET-290): one per character for as long as the screen is open, shared by every
/// place that draws them — a crew stack, a sub-row, a RUNNING line, an idle avatar. A character who starts a run keeps
/// the portrait already loaded, and a recycled list container binds to this rather than holding a bitmap of its own.
/// </summary>
public sealed partial class CharacterFaceViewModel(long characterId, string name) : ObservableObject, IHexFace
{
    IImage? IHexFace.Portrait => Portrait;

    public long CharacterId { get; } = characterId;

    public string Name { get; } = name;

    /// <summary>First letter of the name — the hex's fallback while there is no portrait, the same as every other hex
    /// in the app, so "no ESI link", "images off" and "still loading" all read the same way.</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    [ObservableProperty] private Bitmap? _portrait;

    /// <summary>Best-effort, from the same 64 px cache as the fleet roster and the character picker (ET-184). The
    /// provider reads a setting and maybe a file before it answers, so it is asked off the UI thread; only the result
    /// lands here.</summary>
    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits)
    {
        if (CharacterId is <= 0 or > int.MaxValue)
            return;

        int id = (int)CharacterId;
        Portrait = await Task.Run(() => portraits.GetPortraitAsync(id, 64));
    }
}
