using System;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi.Dtos;
using Microsoft.AspNetCore.SignalR;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// SignalR hub mirroring the <c>/ws</c> stream for richer .NET/JS clients (e.g. <c>@microsoft/signalr</c> with
/// auto-reconnect). The broadcaster invokes the same named events (<c>snapshot</c>/<c>metrics</c>/<c>fleet.metrics</c>/
/// <c>fleet.changed</c>) on <c>Clients.All</c>; on connect this hub sends the caller its own <c>snapshot</c> and bumps
/// the broadcaster's listener count so the 1 Hz tick runs while any client (WS or hub) is connected. Read-only — it
/// exposes no callable server methods.
/// </summary>
public sealed class FleetHub(LocalApiQueries queries, LocalApiBroadcaster broadcaster) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var snapshot = new WsSnapshotDto(
            await queries.GetMetricsAsync(Context.ConnectionAborted),
            await queries.GetActiveFleetAsync(Context.ConnectionAborted));
        await Clients.Caller.SendAsync("snapshot", snapshot, Context.ConnectionAborted);
        broadcaster.SignalRConnected();
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broadcaster.SignalRDisconnected();
        return base.OnDisconnectedAsync(exception);
    }
}
