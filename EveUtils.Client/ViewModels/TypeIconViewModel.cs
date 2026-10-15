using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels;

/// <summary>The icon of one type in a list row (ET-504): the image the provider has, or the bundled placeholder when it
/// has none or images are off — the same fallback ET-496 gave the loot lines. The provider picks the /bp asset for a
/// blueprint itself.</summary>
public sealed partial class TypeIconViewModel(int typeId) : ObservableObject
{
    private const int IconSize = 32;

    public int TypeId { get; } = typeId;

    [ObservableProperty] private Bitmap? _source;

    public async Task LoadAsync(ITypeImageProvider? images)
    {
        Bitmap? image = images is null ? null : await images.GetImageAsync(TypeId, TypeImageKind.Icon, IconSize);
        Source = image ?? TypeImagePlaceholder.Bitmap;
    }
}
