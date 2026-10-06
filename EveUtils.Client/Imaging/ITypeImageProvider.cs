using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace EveUtils.Client.Imaging;

/// <summary>
/// Loads CCP image-server type images (module/charge icons, ship renders) for the fit-detail wheel. Local-first:
/// the network fetch is opt-in (default off) and every image is cached on disk, so a fit you have opened before renders
/// offline. Returns null when images are disabled, not yet cached and offline, or the fetch fails — the caller then
/// falls back to the offline glyph/silhouette.
/// </summary>
public interface ITypeImageProvider
{
    /// <summary>Whether the user has opted in to loading type images from the CCP image server.</summary>
    Task<bool> AreImagesEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The cached or freshly fetched image for a type, or null when unavailable.</summary>
    Task<Bitmap?> GetImageAsync(int typeId, TypeImageKind kind, int size, CancellationToken cancellationToken = default);
}
