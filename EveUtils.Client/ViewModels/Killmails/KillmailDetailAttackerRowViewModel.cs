using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One ATTACKERS row on the killmail detail screen (ET-333): who, their ship/weapon, damage and share,
/// with the FINAL BLOW, TOP DAMAGE and YOU badges — the same three the mockup marks on a row of its own.</summary>
public sealed partial class KillmailDetailAttackerRowViewModel(
    string name, string subText, string shipText, string? weaponText, int damageDone, double sharePercent,
    bool isFinalBlow, bool isTopDamage, bool isYou, bool isNpc, int? characterId, int? shipTypeId,
    int? corporationId) : ObservableObject
{
    public string Name { get; } = name;

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    public async Task LoadImageAsync(ITypeImageProvider images, ICharacterPortraitProvider portraits)
    {
        if (characterId is { } pilot)
        {
            Image = await portraits.GetPortraitAsync(pilot, 64);
        }
        else
        {
            Image = shipTypeId is { } ship ? await images.GetImageAsync(ship, TypeImageKind.Render, 64) : null;
            if (Image is null && corporationId is { } corporation)
            {
                Image = await portraits.GetCorporationLogoAsync(corporation, 64);
            }
        }
    }

    public string SubText { get; } = subText;

    public string ShipText { get; } = shipText;

    public string? WeaponText { get; } = weaponText;

    public string DamageText { get; } = damageDone.ToString("N0", CultureInfo.InvariantCulture);

    public string PercentText { get; } = sharePercent.ToString("0", CultureInfo.InvariantCulture) + "%";

    public bool IsFinalBlow { get; } = isFinalBlow;

    public bool IsTopDamage { get; } = isTopDamage;

    public bool IsYou { get; } = isYou;

    public bool IsNpc { get; } = isNpc;
}
