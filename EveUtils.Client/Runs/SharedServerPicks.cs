using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>The server the pilot picked by hand on the source chip, remembered across sessions (ET-512 decision 10).</summary>
public interface ISharedServerPicks
{
    /// <summary>The address of the preferred server, null when the pilot never picked one.</summary>
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);

    Task SetAsync(string serverAddress, CancellationToken cancellationToken = default);
}

/// <summary>
/// One preferred server for every activity, not one per activity: a setting value holds 4000 characters, which a pick
/// per site would outgrow, and "use the Krah server when it has a figure" is what a pilot who switches means.
/// </summary>
public sealed class SharedServerPicks(IServiceScopeFactory scopeFactory) : ISharedServerPicks, ISingletonService
{
    public const string SettingKey = "runs.outcomes.preferred-server";

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        string? address = (await dispatcher.Query(new GetSettingsQuery(), cancellationToken))
            .FirstOrDefault(setting => setting.Key == SettingKey)?.Value;
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    public async Task SetAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new SetSettingCommand(SettingKey, serverAddress), cancellationToken);
    }
}
