using EveUtils.Server.Grpc;
using EveUtils.Server.Permissions;
using EveUtils.Server.Transport;
using EveUtils.Shared.Modules.Permissions.Repositories;
using Microsoft.AspNetCore.Components;

namespace EveUtils.Server.Components.Pages;

public partial class Dashboard : ComponentBase, IDisposable
{
    [Inject] private ConnectedClients ConnectedClients { get; set; } = default!;
    [Inject] private ServerCertificateInfo CertificateInfo { get; set; } = default!;
    [Inject] private IPermissionToggleStore Toggles { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();

    private string Fingerprint => CertificateInfo.Fingerprint;
    private bool FitSyncEnabled
    {
        get => Toggles.IsEnabled(EveUtils.Shared.Modules.Fittings.FittingsPermissions.Sync);
        set => Toggles.SetEnabled(EveUtils.Shared.Modules.Fittings.FittingsPermissions.Sync, value);
    }
    private bool FitManageEnabled
    {
        get => Toggles.IsEnabled(EveUtils.Shared.Modules.Fittings.FittingsPermissions.Manage);
        set => Toggles.SetEnabled(EveUtils.Shared.Modules.Fittings.FittingsPermissions.Manage, value);
    }
    private IReadOnlyList<ConnectedClientInfo> Connected { get; set; } = [];

    protected override void OnInitialized()
    {
        Connected = ConnectedClients.Snapshot();
        _ = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                Connected = ConnectedClients.Snapshot();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
