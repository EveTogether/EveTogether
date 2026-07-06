using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// A combat booster being simulated on the fit-detail window. Boosters are not stored in the fit; the user adds them
/// here as a what-if overlay (the in-game / eveship.fit simulator model). An active booster is applied as a
/// char-anchored implant so its primary bonuses flow into the stats (side-effects stay gated off); toggling it
/// off or removing it recomputes the fit.
/// </summary>
public sealed partial class BoosterViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;
    private readonly Func<Task> _onChanged;
    private readonly Func<BoosterViewModel, Task> _onRemove;

    public int TypeId { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool _isActive = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    /// <summary>Green when the booster is applied, muted grey when toggled off.</summary>
    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(IsActive ? "#8AE04A" : "#6E7681"));

    public BoosterViewModel(int typeId, string name, bool isActive, ITypeImageProvider? images,
        Func<Task> onChanged, Func<BoosterViewModel, Task> onRemove)
    {
        TypeId = typeId;
        Name = name;
        _isActive = isActive;
        _images = images;
        _onChanged = onChanged;
        _onRemove = onRemove;
    }

    // Toggling the switch re-applies/removes the booster's bonuses; recompute the fit.
    partial void OnIsActiveChanged(bool value) => _ = _onChanged();

    [RelayCommand]
    private Task Remove() => _onRemove(this);

    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
