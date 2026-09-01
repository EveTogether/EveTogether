using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One line in a fit row's equipment popover (and the table's per-rack tooltips): icon + name + stacked quantity.
/// The icon loads on demand (<see cref="LoadImageAsync"/>) so a row nobody hovers fetches no images.
/// </summary>
public sealed partial class FitModuleLineViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;

    public int TypeId { get; }
    public string Name { get; }

    /// <summary>How many of this item sit on the line — one for a fitted module, more for drones, charges and
    /// cargo, which is where the popover needs it.</summary>
    public int Quantity { get; }

    /// <summary>Shown only when the line stacks: a lone module carries no "×1".</summary>
    public string QuantityLabel => Quantity > 1 ? $"×{Quantity}" : "";

    public bool IsStacked => Quantity > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    public FitModuleLineViewModel(int typeId, string name, ITypeImageProvider? images, int quantity = 1)
    {
        TypeId = typeId;
        Name = name;
        _images = images;
        Quantity = quantity;
    }

    /// <summary>The same line carrying <paramref name="extra"/> more of the item — how a rack folds its duplicate
    /// modules onto one line. A fresh instance rather than a mutation: the ungrouped lines are still on screen in
    /// the table's rack tooltips.</summary>
    public FitModuleLineViewModel Plus(int extra) =>
        new(TypeId, Name, _images, Quantity + extra) { Image = Image };

    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
