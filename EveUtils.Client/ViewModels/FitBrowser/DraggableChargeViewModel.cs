using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A compatible charge shown in the fit-detail Charges strip (2f): drag it onto a module to load it there, or
/// onto the wheel centre to load it on every module that accepts it. The icon loads lazily from the CCP image server.</summary>
public sealed partial class DraggableChargeViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;

    public int TypeId { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    public DraggableChargeViewModel(int typeId, string name, ITypeImageProvider? images)
    {
        TypeId = typeId;
        Name = name;
        _images = images;
    }

    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
