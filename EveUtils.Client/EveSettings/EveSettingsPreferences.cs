using System.Globalization;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// The two things about EVE's settings folder that only the user can tell us, kept in the ordinary client settings
/// store so they survive a restart:
///
/// <list type="bullet">
/// <item>the install directory, when auto-detection found nothing and the user pointed at it themselves;</item>
/// <item>the name of an account — EVE publishes none for an account id, so the name the user gives it is the only
/// one there will ever be, and re-asking every session would be worse than showing the id.</item>
/// </list>
///
/// A short-lived scope per call (like <see cref="Fleet.MetricShareSettings"/>): the repository is scoped, this is not.
/// </summary>
public sealed class EveSettingsPreferences(IServiceProvider services) : ISingletonService
{
    private const string InstallRootKey = "eve-settings.install-root";
    private const string AccountNamePrefix = "eve-settings.account-name.";

    /// <summary>The directory the user picked, or null when they never had to.</summary>
    public async Task<string?> LoadInstallRootAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _ListAsync(cancellationToken);
        var stored = settings.FirstOrDefault(setting => setting.Key == InstallRootKey)?.Value;
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    public Task SaveInstallRootAsync(string installRoot, CancellationToken cancellationToken = default) =>
        _UpsertAsync(InstallRootKey, installRoot.Trim(), cancellationToken);

    public async Task<IReadOnlyDictionary<long, string>> LoadAccountNamesAsync(CancellationToken cancellationToken = default)
    {
        var names = new Dictionary<long, string>();
        foreach (var setting in await _ListAsync(cancellationToken))
        {
            if (!setting.Key.StartsWith(AccountNamePrefix, StringComparison.Ordinal))
                continue;
            if (long.TryParse(setting.Key.AsSpan(AccountNamePrefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var accountId))
                names[accountId] = setting.Value;
        }

        return names;
    }

    public Task SaveAccountNameAsync(long accountId, string name, CancellationToken cancellationToken = default) =>
        _UpsertAsync(AccountNamePrefix + accountId.ToString(CultureInfo.InvariantCulture), name.Trim(), cancellationToken);

    private async Task<IReadOnlyList<Shared.Modules.Settings.Entities.ClientSetting>> _ListAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingRepository>().ListAsync(cancellationToken);
    }

    private async Task _UpsertAsync(string key, string value, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISettingRepository>().UpsertAsync(key, value, cancellationToken);
    }
}
