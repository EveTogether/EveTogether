using EveUtils.Server.DataExplorer;
using EveUtils.Server.Permissions;
using EveUtils.Server.Transport;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.Permissions.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace EveUtils.Server.Components.Pages;

public partial class Dashboard : ComponentBase
{
    [Inject] private ServerCertificateInfo CertificateInfo { get; set; } = default!;
    [Inject] private IPermissionToggleStore Toggles { get; set; } = default!;
    [Inject] private DashboardOverviewService Overview { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthState { get; set; } = default!;
    [Inject] private IAuthorizationService Authorization { get; set; } = default!;

    private DashboardOverview? _overview;
    private bool _canViewData;

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

    // The tiles and the attention list carry names and ids from the Data pages, so they need the same permission.
    protected override async Task OnInitializedAsync()
    {
        var user = (await AuthState.GetAuthenticationStateAsync()).User;
        _canViewData = (await Authorization.AuthorizeAsync(user, PanelPermissions.DataView)).Succeeded;
        if (_canViewData)
            _overview = await Overview.GetOverviewAsync();
    }
}
