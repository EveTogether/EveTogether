using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace EveUtils.Client.Imaging;

/// <summary>
/// Loads a character's ESI portrait render (<c>images.evetech.net/characters/{id}/portrait</c>) for the hex
/// portraits in the character column. Gated behind the same opt-in image setting
/// as type images so a fully-offline user gets the glyph fallback. Returns null on any failure.
/// </summary>
public interface ICharacterPortraitProvider
{
    Task<Bitmap?> GetPortraitAsync(int characterId, int size, CancellationToken cancellationToken = default);
}
