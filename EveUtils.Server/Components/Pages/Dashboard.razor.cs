using EveUtils.Server.Grpc;
using EveUtils.Server.Permissions;
using EveUtils.Server.Transport;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using Microsoft.AspNetCore.Components;

namespace EveUtils.Server.Components.Pages;

public partial class Dashboard : ComponentBase, IDisposable
{
    [Inject] private ConnectedClients ConnectedClients { get; set; } = default!;
    [Inject] private ServerCertificateInfo CertificateInfo { get; set; } = default!;
    [Inject] private IServerAuthRepository Repository { get; set; } = default!;
    [Inject] private ISharedFitRepository SharedFitRepository { get; set; } = default!;
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
    private IReadOnlyList<ServerSession> Sessions { get; set; } = [];
    private IReadOnlyList<SyncedCharacter> Synced { get; set; } = [];
    private IReadOnlyList<SharedFit> SharedFits { get; set; } = [];

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _ = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await LoadAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAsync()
    {
        Connected = ConnectedClients.Snapshot();
        Sessions = await Repository.ListSessionsAsync(_cts.Token);
        Synced = await Repository.ListSyncedAsync(_cts.Token);
        SharedFits = await SharedFitRepository.ListAsync(_cts.Token);
    }

    private static bool IsStale(DateTimeOffset lastHeartbeat) =>
        DateTimeOffset.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(60);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
