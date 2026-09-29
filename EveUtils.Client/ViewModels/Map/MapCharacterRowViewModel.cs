using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>One of your own characters in the FOLLOW list: who, where the map last placed them, and who said so.</summary>
public sealed partial class MapCharacterRowViewModel(string name, int characterId) : ObservableObject
{
    public string Name { get; } = name;

    public int CharacterId { get; } = characterId;

    public string Initial => Name.Length == 0 ? "?" : Name[..1].ToUpperInvariant();

    public bool HasPortrait => Portrait is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortrait))]
    private Bitmap? _portrait;

    [ObservableProperty] private string _systemName = "location unknown";
    [ObservableProperty] private string _securityText = string.Empty;
    [ObservableProperty] private IBrush _securityBrush = MapPalette.MutedBrush;
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private bool _isFollowed;

    public void ShowPosition(FleetPositionDto? position, MapSystemDto? system)
    {
        if (position is null || system is null)
        {
            SystemName = "location unknown";
            SecurityText = string.Empty;
            SecurityBrush = MapPalette.MutedBrush;
            SourceText = string.Empty;
            return;
        }

        SystemName = system.Name;
        SecurityText = system.DisplaySecurity.ToString("0.0", CultureInfo.InvariantCulture);
        SecurityBrush = MapPalette.SecurityBrush(system.DisplaySecurity);
        SourceText = position.Source switch
        {
            PositionSource.Gamelog => "game log",
            PositionSource.EsiLocation => "ESI location",
            _ => string.Empty
        };
    }

    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits, CancellationToken cancellationToken = default) =>
        Portrait = await portraits.GetPortraitAsync(CharacterId, 64, cancellationToken);
}
