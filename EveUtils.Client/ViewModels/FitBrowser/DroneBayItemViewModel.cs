using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Dogma;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// A drone stack in the bay on the fit-detail Drone Bay panel: its icon, name, how many are in the bay and how many of
/// them are deployed (active). The in-game "Selected:" checkbox row (one box per drone in the stack) drives which drones
/// are "in space"; the window caps the deployed count at the universal 5-drone limit and the ship's bandwidth and
/// recomputes drone DPS for the new selection.
/// </summary>
public sealed partial class DroneBayItemViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;
    private readonly Func<DroneBayItemViewModel, int, Task> _onRequestActive;   // (stack, desired active count)

    public int TypeId { get; }
    public string Name { get; }
    public int BayQuantity { get; }
    public double BandwidthPerDrone { get; }
    public string QuantityLabel => $"×{BayQuantity}";

    /// <summary>One checkbox per drone in the bay; the first <see cref="ActiveQuantity"/> read as selected (in space).</summary>
    public IReadOnlyList<DroneSelectionSlotViewModel> Slots { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLabel))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private int _activeQuantity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;
    public bool IsActive => ActiveQuantity > 0;
    public string ActiveLabel => $"{ActiveQuantity} / {BayQuantity}";

    // The deployed drone's own resolved per-drone readout (set after each recompute); null falls the tooltip back to
    // just the name + deployment line (a drone that is not in space has no contribution).
    private ModuleContribution? _contribution;

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { $"{Name} — {ActiveQuantity} of {BayQuantity} deployed" };
            if (_contribution is { } contribution)
            {
                if (Math.Round(contribution.OptimalRange / 1000.0, 1) > 0)
                    lines.Add($"Optimal {contribution.OptimalRange / 1000.0:0.0} km");
                if (Math.Round(contribution.FalloffRange / 1000.0, 1) > 0)   // hide the attr-158 default (1 m) that rounds to 0.0 km
                    lines.Add($"Falloff {contribution.FalloffRange / 1000.0:0.0} km");
                if (contribution.Dps > 0)
                    lines.Add($"Damage Per Second {contribution.Dps:0.0}");
                if (contribution.MiningYieldPerSec > 0)
                    lines.Add($"{contribution.MiningYieldPerSec:0.0} m³/s");
                if (contribution.TrackingSpeed > 0)
                    lines.Add($"Tracking {contribution.TrackingSpeed:0.000}");
            }
            lines.Add($"Bandwidth Needed {BandwidthPerDrone:0.#} Mbit/sec");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Hands this drone stack its own per-drone contribution so the bay tooltip shows the in-game
    /// readout (DPS, range, tracking or mining yield); null when the drone is not deployed.</summary>
    public void SetContribution(ModuleContribution? contribution)
    {
        _contribution = contribution;
        OnPropertyChanged(nameof(Tooltip));
    }

    public DroneBayItemViewModel(int typeId, string name, int bayQuantity, double bandwidthPerDrone,
        ITypeImageProvider? images, Func<DroneBayItemViewModel, int, Task> onRequestActive)
    {
        TypeId = typeId;
        Name = name;
        BayQuantity = bayQuantity;
        BandwidthPerDrone = bandwidthPerDrone;
        _images = images;
        _onRequestActive = onRequestActive;
        Slots = Enumerable.Range(1, bayQuantity)
            .Select(position => new DroneSelectionSlotViewModel(position, isSelected: false,
                desiredActive => _ = _onRequestActive(this, desiredActive)))
            .ToList();
    }

    // The window sets ActiveQuantity after clamping the request to the drone limit and bandwidth; mirror it onto the
    // checkbox row so a box that could not deploy snaps back to unchecked.
    partial void OnActiveQuantityChanged(int value)
    {
        foreach (var slot in Slots)
            slot.SetSelected(slot.Position <= value);
    }

    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(TypeId, TypeImageKind.Icon, 32);
}
