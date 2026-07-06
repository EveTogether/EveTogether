using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A stack of items in the fit's cargo hold, shown in the fit-detail cargo strip: icon, name and quantity.</summary>
public sealed partial class CargoItemViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;

    public int TypeId { get; }
    public string Name { get; }
    public int Quantity { get; }
    public string QuantityLabel => Quantity > 1 ? $"×{Quantity}" : "";
    public string Tooltip => Quantity > 1 ? $"{Name} ×{Quantity}" : Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    public CargoItemViewModel(int typeId, string name, int quantity, ITypeImageProvider? images)
    {
        TypeId = typeId;
        Name = name;
        Quantity = quantity;
        _images = images;
    }

    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
