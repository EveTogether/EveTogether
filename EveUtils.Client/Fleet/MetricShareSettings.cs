using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Reads the persisted client settings (a short-lived scope per call, like <see cref="LocationMetricSource"/>) and
/// snapshots the per-metric share decisions. Single source of truth for "what do I share with the fleet", consulted
/// by the publisher each tick and (later) bound to per-metric checkboxes in the UI.
/// </summary>
public sealed class MetricShareSettings(IServiceProvider services) : IMetricShareSettings, ISingletonService
{
    public async Task<MetricShareSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var settings = await dispatcher.Query(new GetSettingsQuery(), cancellationToken);
        var values = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        return new MetricShareSnapshot(values);
    }
}
