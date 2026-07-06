using Microsoft.AspNetCore.SignalR;

namespace EveUtils.Server.Stream;

/// <summary>
/// SignalR hub for the live DPS stream page. Deliberately separate from the Blazor panel — it
/// exercises the SignalR/API exposure path that v2.x needs for external apps / Twitch widgets.
/// </summary>
public sealed class DpsHub : Hub;
