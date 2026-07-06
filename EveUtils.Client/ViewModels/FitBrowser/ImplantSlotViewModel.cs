using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One applied implant shown in the fit-detail IMPLANTS panel: icon + name, read-only. The implants
/// come from the selected source (the fit's own or a character's), so there is no add/remove here.</summary>
public sealed partial class ImplantSlotViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public ImplantSlotViewModel(int typeId, string name, ITypeImageProvider? images)
    {
        TypeId = typeId;
        Name = name;
        _images = images;
    }

    public int TypeId { get; }
    public string Name { get; }
    public bool HasImage => Image is not null;

    public async Task LoadImageAsync()
    {
        if (_images is null || !await _images.AreImagesEnabledAsync())
            return;
        Image = await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 64);
    }
}
