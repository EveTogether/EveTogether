using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A group of same-type charge-capable modules in the Charges panel's filter row: the four identical turrets
/// show as one icon. It shows only the module's own icon — not whatever charge is loaded — and its single action
/// is to filter the charge list to this module type. Picking a charge loads it on every module of the type, the way you
/// ammo up identical guns in-game.</summary>
public sealed partial class ChargeModuleGroupViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ModuleSlotViewModel> _modules;
    private readonly ITypeImageProvider? _images;

    public int TypeId { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    [ObservableProperty]
    private bool _isSelected;

    public ChargeModuleGroupViewModel(IReadOnlyList<ModuleSlotViewModel> modules, ITypeImageProvider? images)
    {
        _modules = modules;
        _images = images;
        TypeId = modules[0].TypeId;
        Name = modules[0].Name;
    }

    /// <summary>The charges this module type accepts (same for every module in the group).</summary>
    public IReadOnlyList<SdeChargeType> ChargeOptions => _modules[0].ChargeOptions;

    public bool AcceptsCharge(int chargeTypeId) => _modules[0].AcceptsCharge(chargeTypeId);

    /// <summary>Loads the charge on every module of this type (identical guns share ammo, in-game style).</summary>
    public async Task LoadChargeAsync(int chargeTypeId)
    {
        foreach (var module in _modules)
            await module.LoadChargeAsync(chargeTypeId);
    }

    /// <summary>Loads the module's own type icon — the filter shows the module, never the loaded charge.</summary>
    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
