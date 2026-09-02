using EveUtils.Server.Auth;
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
    private IReadOnlyList<SessionRow> Sessions { get; set; } = [];
    private IReadOnlyList<SyncedCharacter> Synced { get; set; } = [];
    private IReadOnlyList<SharedFit> SharedFits { get; set; } = [];

    /// <summary>The session the operator asked to revoke, held until they confirm — the same two-step the Data
    /// page uses, and worth more here: on this page most rows belong to someone's second or third machine.</summary>
    private SessionRow? PendingRevoke { get; set; }

    private int LiveSessions => Sessions.Count(s => s.IsLive);
    private int LapsingSessions => Sessions.Count(s => s.IsLapsing);

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
        var now = DateTimeOffset.UtcNow;
        Connected = ConnectedClients.Snapshot();
        Synced = await Repository.ListSyncedAsync(_cts.Token);
        SharedFits = await SharedFitRepository.ListAsync(_cts.Token);

        // Grouped by character and most-recently-seen first: one character legitimately has a session per machine
        // it is paired on, and this is where the operator checks that all of them are still there.
        Sessions = (await Repository.ListSessionsAsync(_cts.Token))
            .Select(s => new SessionRow(s, now - s.LastHeartbeat))
            .OrderBy(r => r.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.Session.LastHeartbeat)
            .ToList();
    }

    private async Task RevokeAsync()
    {
        if (PendingRevoke is not { } row)
            return;

        PendingRevoke = null;
        await Repository.DeleteSessionAsync(row.Session.Id, _cts.Token);
        await LoadAsync();
    }

    /// <summary>
    /// One session as the panel shows it. <see cref="Silence"/> — not "is there a newer session for this
    /// character" — is the only thing that says whether anything still uses this row, because several machines
    /// may hold a live session for the same character at once.
    /// </summary>
    private sealed record SessionRow(ServerSession Session, TimeSpan Silence)
    {
        /// <summary>Two missed 30s heartbeats: attached right now rather than merely refreshable.</summary>
        private static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(60);

        public string CharacterName => Session.SyncedCharacter?.CharacterName ?? "—";
        public bool IsLive => Silence <= LiveWindow;

        /// <summary>Past the idle window, so the next cleanup sweep removes it. Says why a row is about to vanish.</summary>
        public bool IsLapsing => Silence >= ServerSessionService.IdleLifetime;

        public string State => IsLive ? "live" : IsLapsing ? "lapsing" : "idle";
        public string StateClass => IsLive ? "ok" : IsLapsing ? "stale" : "dim";

        /// <summary>How long since anything used this session, at the coarsest unit that still reads at a glance.</summary>
        public string SilenceLabel =>
            Silence < TimeSpan.FromMinutes(1) ? $"{Silence.TotalSeconds:0}s"
            : Silence < TimeSpan.FromHours(1) ? $"{Silence.TotalMinutes:0}m"
            : Silence < TimeSpan.FromDays(1) ? $"{Silence.TotalHours:0}h"
            : $"{Silence.TotalDays:0}d";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
