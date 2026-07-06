using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One fitted module in a fit-row rack tooltip: icon + name. The icon loads on demand
/// (<see cref="LoadImageAsync"/>) so a row whose tooltip is never opened fetches no images.
/// </summary>
public sealed partial class FitModuleLineViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;

    public int TypeId { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    public FitModuleLineViewModel(int typeId, string name, ITypeImageProvider? images)
    {
        TypeId = typeId;
        Name = name;
        _images = images;
    }

    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
