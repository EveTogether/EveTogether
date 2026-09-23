using System.Collections.Concurrent;
using Avalonia.Threading;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One face per character for a whole screen, own and external alike: the hex shows the initial until the
/// portrait lands, best-effort and fire-and-forget the same as a fleet roster leaf (ET-184, ET-306) — posted to the UI
/// thread regardless of which thread creates the face, since a crew face is as likely to be minted from an off-thread
/// read as from the UI thread building the running lanes. The runs overview and the home each keep one.</summary>
public sealed class CharacterFaceCache(ICharacterPortraitProvider? portraits)
{
    private readonly ConcurrentDictionary<long, CharacterFaceViewModel> _faces = new();

    public CharacterFaceViewModel FaceOf(long characterId, string name) =>
        _faces.GetOrAdd(characterId, id =>
        {
            var face = new CharacterFaceViewModel(id, name);
            if (portraits is not null)
                Dispatcher.UIThread.Post(() => _ = face.LoadPortraitAsync(portraits));
            return face;
        });
}
