using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Client.ViewModels;

public sealed class FittingViewModel
{
    public int Id            { get; }
    public int EsiFittingId  { get; }
    public string Name       { get; }
    public int ShipTypeId    { get; }
    public string OwnerId     { get; }
    public string OwnerName   { get; } // source character (display only); fits are portable

    public ICommand PushCommand   { get; }
    public ICommand ShareCommand  { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }

    public FittingViewModel(
        LocalFitting fitting, string ownerName,
        Func<FittingViewModel, Task> onPush,
        Func<FittingViewModel, Task> onShare,
        Func<FittingViewModel, Task> onDelete,
        Func<FittingViewModel, Task> onExport)
    {
        Id           = fitting.Id;
        EsiFittingId = fitting.EsiFittingId;
        Name         = fitting.Name;
        ShipTypeId   = fitting.ShipTypeId;
        OwnerId      = fitting.OwnerId;
        OwnerName    = ownerName;
        PushCommand   = new AsyncRelayCommand(() => onPush(this));
        ShareCommand  = new AsyncRelayCommand(() => onShare(this));
        DeleteCommand = new AsyncRelayCommand(() => onDelete(this));
        ExportCommand = new AsyncRelayCommand(() => onExport(this));
    }
}
