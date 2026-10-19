using System.Text.Json;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>Where the pilot stands on sharing run outcomes with one server (ET-515).</summary>
public enum OutcomeSharingChoice
{
    /// <summary>Not asked yet, or asked and answered "not now": nothing is shared.</summary>
    Undecided,
    Shared,
    NotShared,

    /// <summary>Answered "never" to the one question: nothing is shared and it is not asked again. The settings can still switch it on.</summary>
    Never
}

/// <summary>The choice per server address, kept in one setting: an address is too long for a setting key of its own.
/// Nothing is shared with a server that has no entry (opt-in, ET-512).</summary>
public sealed class RunOutcomeSharingChoices(IServiceScopeFactory scopeFactory) : ISingletonService
{
    public const string SettingKey = "runs.outcomes.sharing";

    public async Task<IReadOnlyDictionary<string, OutcomeSharingChoice>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        return _Read(await dispatcher.Query(new GetSettingsQuery(), cancellationToken));
    }

    public async Task SetAsync(string serverAddress, OutcomeSharingChoice choice, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        Dictionary<string, OutcomeSharingChoice> choices = new(_Read(await dispatcher.Query(new GetSettingsQuery(), cancellationToken)),
            StringComparer.Ordinal)
        {
            [serverAddress] = choice
        };
        await dispatcher.Send(new SetSettingCommand(SettingKey, JsonSerializer.Serialize(choices)), cancellationToken);
    }

    private static IReadOnlyDictionary<string, OutcomeSharingChoice> _Read(IReadOnlyList<SettingDto> settings)
    {
        string? json = settings.FirstOrDefault(setting => setting.Key == SettingKey)?.Value;
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, OutcomeSharingChoice>(StringComparer.Ordinal);

        try
        {
            return new Dictionary<string, OutcomeSharingChoice>(
                JsonSerializer.Deserialize<Dictionary<string, OutcomeSharingChoice>>(json) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // An unreadable value reads as "never chosen": nothing is shared until the pilot says so again.
            return new Dictionary<string, OutcomeSharingChoice>(StringComparer.Ordinal);
        }
    }
}
