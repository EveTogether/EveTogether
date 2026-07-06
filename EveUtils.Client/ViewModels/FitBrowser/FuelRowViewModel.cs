namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One online-able service module in a structure's fuel panel: its name, whether it is currently online (and so burning
/// fuel), and its hourly fuel-block draw (SDE attr 2109, a one-hour cycle) plus the one-off cost to bring it online
/// (attr 2110). Offline modules show no draw, mirroring the in-game structure fitting.
/// </summary>
public sealed class FuelRowViewModel(string name, bool isOnline, double fuelPerHour, double onlineCost)
{
    public string Name { get; } = name;
    public bool IsOnline { get; } = isOnline;

    /// <summary>Hourly fuel draw while online; "offline" when the service is not online.</summary>
    public string FuelLabel => IsOnline ? $"{fuelPerHour:0} /h" : "offline";

    public string OnlineCostLabel => $"online: {onlineCost:0}";
}
