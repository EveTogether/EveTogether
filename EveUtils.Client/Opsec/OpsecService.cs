using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Opsec;

public sealed class OpsecService(IServiceProvider services) : IOpsecService
{
    /// <summary>Settings key for the persisted state. Default off: only "true" turns it on.</summary>
    public const string EnabledSettingKey = "opsec.enabled";

    private readonly OpsecMask _mask = OpsecMask.ForThisSession();

    public bool IsEnabled { get; private set; }

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetSettingsQuery(), cancellationToken);

        var saved = settings.FirstOrDefault(s => s.Key == EnabledSettingKey)?.Value;
        _Apply(string.Equals(saved, "true", StringComparison.OrdinalIgnoreCase));
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        // Applied before it is saved: on a stream, the mask going up is what matters, the setting can follow.
        _Apply(enabled);

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(EnabledSettingKey, enabled ? "true" : "false"), cancellationToken);
    }

    [return: NotNullIfNotNull(nameof(text))]
    public string? Render(string? text) =>
        IsEnabled ? OpsecText.Replace(text, _mask.Mask) : OpsecText.Strip(text);

    private void _Apply(bool enabled)
    {
        if (enabled == IsEnabled)
            return;

        IsEnabled = enabled;
        Changed?.Invoke();
    }
}
