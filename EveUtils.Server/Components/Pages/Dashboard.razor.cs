using EveUtils.Server.DataExplorer;
using EveUtils.Server.Permissions;
using EveUtils.Server.Transport;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.RunOutcomes;
using EveUtils.Shared.Modules.RunOutcomes.Queries;
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

    [Inject] private IDispatcher Dispatcher { get; set; } = default!;

    private DashboardOverview? _overview;
    private bool _canViewData;
    private int _runOutcomeCount;

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

    private bool RunOutcomesEnabled
    {
        get => Toggles.IsEnabled(RunOutcomesModule.EnabledToggle);
        set => Toggles.SetEnabled(RunOutcomesModule.EnabledToggle, value);
    }

    // The tiles and the attention list carry names and ids from the Data pages, so they need the same permission. The
    // outcome count names nobody.
    protected override async Task OnInitializedAsync()
    {
        _runOutcomeCount = (await Dispatcher.Query(new CountRunOutcomesQuery())).Value;
        var user = (await AuthState.GetAuthenticationStateAsync()).User;
        _canViewData = (await Authorization.AuthorizeAsync(user, PanelPermissions.DataView)).Succeeded;
        if (_canViewData)
            _overview = await Overview.GetOverviewAsync();
    }
}
