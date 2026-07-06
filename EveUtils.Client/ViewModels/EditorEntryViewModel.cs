using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A fit entry inside a role group in the composition editor. <see cref="Id"/> is null for a fit added during
/// this edit session (persisted on save). The <see cref="FitReferenceInfo"/> snapshot is immutable — changing a fit
/// means removing this entry and picking another — so only the optional per-fit minimum is editable here.
/// </summary>
public sealed partial class EditorEntryViewModel : ObservableObject
{
    private readonly ITypeImageProvider? _images;

    public EditorEntryViewModel(long? id, FitReferenceInfo fit, string hullName, int? entryMinCount,
        ITypeImageProvider? images = null)
    {
        Id = id;
        Fit = fit;
        HullName = hullName;
        _minText = CompositionMinValue.Format(entryMinCount);
        _images = images;
    }

    public long? Id { get; }
    public FitReferenceInfo Fit { get; }
    public string FitName => Fit.FitName;
    public string HullName { get; }

    [ObservableProperty] private string _minText;

    /// <summary>The parsed per-fit minimum, or null when the field is blank.</summary>
    public int? EntryMinCount => CompositionMinValue.Parse(MinText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHullImage))]
    private Bitmap? _hullImage;

    public bool HasHullImage => HullImage is not null;

    /// <summary>Loads the hull render for the entry icon — on demand, opt-in CCP images; null leaves the
    /// placeholder box.</summary>
    public async Task LoadHullImageAsync() =>
        HullImage = _images is null ? null : await _images.GetImageAsync(Fit.ShipTypeId, TypeImageKind.Render, 64);
}
