using System;
using EveUtils.Client.Opsec;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// The one place the Local API decides whether it may hand out where you are: only when the user turned on
/// "Include my location" (<see cref="LocalApiServer.IncludeLocationSettingKey"/>, read when the host starts), and never
/// while OPSEC mode is on (read live). Registered in the host's container for every endpoint and stream to share.
/// </summary>
public sealed class LocalApiPrivacy(IServiceProvider rootServices, bool includeLocation)
{
    public bool ExposesLocation => includeLocation && rootServices.GetService<IOpsecService>()?.IsEnabled != true;
}
