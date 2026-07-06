using System.Collections.ObjectModel;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One server's section in a fleets list: a header (the coupled server's name) over
/// the fleets that live on that server. The My/Participating/Browser lists become collections of these groups so a
/// character coupled to several servers sees every server's fleets, grouped per server, instead of only the first.
/// </summary>
public sealed class FleetServerGroupViewModel
{
    public FleetServerGroupViewModel(string serverName, string? serverAddress)
    {
        ServerName = serverName;
        ServerAddress = serverAddress;
    }

    /// <summary>The grouping header shown above this server's fleets.</summary>
    public string ServerName { get; }

    /// <summary>The server address these fleets target; null only for the (ungrouped) client-only list.</summary>
    public string? ServerAddress { get; }

    public ObservableCollection<FleetViewModel> Fleets { get; } = [];
}
