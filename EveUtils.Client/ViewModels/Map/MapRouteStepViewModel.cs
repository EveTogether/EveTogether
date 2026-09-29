using System.Globalization;
using Avalonia.Media;
using EveUtils.Client.Controls.Map;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>One row of the planned route's list: the jump number, the system, its region and its security.</summary>
public sealed class MapRouteStepViewModel(int number, RouteStepDto step)
{
    public int Number { get; } = number;

    public RouteStepDto Step { get; } = step;

    public string Name => Step.Name;

    public string RegionName => Step.RegionName;

    public string SecurityText => Step.DisplaySecurity.ToString("0.0", CultureInfo.InvariantCulture);

    public IBrush SecurityBrush => MapPalette.SecurityBrush(Step.DisplaySecurity);
}
