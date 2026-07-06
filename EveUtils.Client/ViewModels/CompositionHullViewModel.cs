using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels;

/// <summary>One ship-hull thumbnail on a composition card: the distinct hulls a doctrine flies, shown as
/// the same small render used in the picker/editor rows. The image loads on demand (opt-in CCP image server) —
/// null leaves the empty frame, like everywhere else.</summary>
public sealed partial class CompositionHullViewModel(int shipTypeId, ITypeImageProvider? images) : ObservableObject
{
    public int ShipTypeId { get; } = shipTypeId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHullImage))]
    private Bitmap? _hullImage;

    public bool HasHullImage => HullImage is not null;

    public async Task LoadAsync() =>
        HullImage = images is null ? null : await images.GetImageAsync(ShipTypeId, TypeImageKind.Render, 64);
}
