using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One selectable fit in the <see cref="FitPickerViewModel"/>: the <see cref="FitReferenceInfo"/> snapshot the
/// picker returns, plus its display fields and multi-select state. A fit already present in the target role group is
/// shown disabled (<see cref="AlreadyAdded"/>) so the same fit is not added to a role twice.
/// </summary>
public sealed partial class FitPickerRowViewModel : ObservableObject
{
    private readonly ITypeImageProvider? _images;

    public FitPickerRowViewModel(FitReferenceInfo fit, string hullName, string source, string owner, bool alreadyAdded,
        ITypeImageProvider? images = null)
    {
        Fit = fit;
        HullName = hullName;
        Source = source;
        Owner = owner;
        AlreadyAdded = alreadyAdded;
        _images = images;
    }

    /// <summary>The snapshot this row contributes when picked — what the editor stores on the composition entry.</summary>
    public FitReferenceInfo Fit { get; }

    public string FitName => Fit.FitName;
    public string HullName { get; }
    public string Source { get; }
    public string Owner { get; }
    public bool AlreadyAdded { get; }

    /// <summary>"Hull · source · owner" sub-line, with a hint when the fit is already in the role group.</summary>
    public string Detail => AlreadyAdded ? $"{HullName} · {Source} · {Owner} · already added" : $"{HullName} · {Source} · {Owner}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHullImage))]
    private Bitmap? _hullImage;

    public bool HasHullImage => HullImage is not null;

    /// <summary>Loads the hull render for the row icon — on demand, opt-in CCP images; null leaves the
    /// placeholder box.</summary>
    public async Task LoadHullImageAsync() =>
        HullImage = _images is null ? null : await _images.GetImageAsync(Fit.ShipTypeId, TypeImageKind.Render, 64);

    [ObservableProperty] private bool _isSelected;

    /// <summary>The member's current assignment in single-select mode — shown as the current marker and not re-pickable.</summary>
    [ObservableProperty] private bool _isCurrent;

    // --- can-fly badge: shown per row in single-mode assign, against the target character. ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFly))]
    [NotifyPropertyChangedFor(nameof(HasSkillGap))]
    [NotifyPropertyChangedFor(nameof(SkillBadgeTooltip))]
    private MemberSkillBadge? _skillBadge;

    public bool CanFly => SkillBadge is { CanFly: true };
    public bool HasSkillGap => SkillBadge is { CanFly: false };
    public string SkillBadgeTooltip => SkillBadge?.Tooltip ?? string.Empty;

    /// <summary>Resolves whether <paramref name="characterId"/> can fly this row's fit (on demand; null verdict =
    /// no badge), so picking a fit shows up-front whether the target pilot has the skills.</summary>
    public async Task LoadSkillBadgeAsync(IMemberFitSkillEvaluator evaluator, int characterId) =>
        SkillBadge = await evaluator.EvaluateAsync(characterId, Fit);

    /// <summary>Whole-row toggle for multi-select (the row is a click target); a fit already in the group can't be
    /// re-selected. Single-select picks through the parent view-model instead.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (!AlreadyAdded)
            IsSelected = !IsSelected;
    }
}
