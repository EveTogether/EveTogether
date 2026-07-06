using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Transport;

namespace EveUtils.Client.Dialogs;

/// <summary>One row in the server-fits browse/manage dialog: download or delete.</summary>
public sealed class ServerFitRowViewModel
{
    public SharedFitInfo Fit { get; }
    public string Name => Fit.Name;
    public int ShipTypeId => Fit.ShipTypeId;
    public string SharedBy => Fit.SharedByCharacterName;

    public ICommand DownloadCommand { get; }
    public ICommand DeleteCommand { get; }

    public ServerFitRowViewModel(SharedFitInfo fit, Func<ServerFitRowViewModel, Task> onDownload, Func<ServerFitRowViewModel, Task> onDelete)
    {
        Fit = fit;
        DownloadCommand = new AsyncRelayCommand(() => onDownload(this));
        DeleteCommand = new AsyncRelayCommand(() => onDelete(this));
    }
}
