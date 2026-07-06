using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Weather/environment selector for the fit simulator: a dropdown of the curated effect beacons (wormhole
/// classes, metaliminal storms, Triglavian invasion, and — once patched — abyssal weather) plus a default "None" row.
/// The parent ViewModel subscribes to <see cref="WeatherChanged"/> to recompute and reads <see cref="CurrentWeather"/>;
/// "None" yields a null selection so the engine injects no weather source and the result is unchanged.
/// </summary>
public sealed class WeatherSelectorViewModel : ViewModelBase
{
    private static readonly WeatherOptionViewModel None = new(null, "None", null);

    public WeatherSelectorViewModel(ISdeAccessor? sde)
    {
        var beacons = sde?.GetEnvironmentBeacons() ?? [];
        Options = new List<WeatherOptionViewModel> { None }
            .Concat(beacons.Select(beacon => new WeatherOptionViewModel(beacon.TypeId, beacon.DisplayName, beacon.Category)))
            .ToList();
        _selectedOption = None;
    }

    /// <summary>The picker entries: "None" first, then the environment beacons ordered by category then tier.</summary>
    public IReadOnlyList<WeatherOptionViewModel> Options { get; }

    private WeatherOptionViewModel _selectedOption;

    /// <summary>The selected option; picking one raises <see cref="WeatherChanged"/> so the parent recomputes.</summary>
    public WeatherOptionViewModel SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value ?? None))
                WeatherChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The engine selection for the current option, or null when "None" is selected.</summary>
    public WeatherInput? CurrentWeather => _selectedOption.ToInput();

    public event EventHandler? WeatherChanged;
}
