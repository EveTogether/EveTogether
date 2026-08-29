using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// Everything about EVE's settings folder that only this machine can tell us, kept in the ordinary client settings
/// store so it survives a restart:
///
/// <list type="bullet">
/// <item>the install directory, when auto-detection found nothing and the user pointed at it themselves;</item>
/// <item>the name of an account — EVE publishes none for an account id, so the name the user gives it is the only
/// one there will ever be, and re-asking every session would be worse than showing the id;</item>
/// <item>which characters sit on which account (ET-64) — inferred once, then remembered, so a later multiboxing
/// session cannot wipe out what a quieter evening made plain;</item>
/// <item>the standing instruction for the automatic sync and the record of what it has done (ET-60).</item>
/// </list>
///
/// A short-lived scope per call (like <see cref="Fleet.MetricShareSettings"/>): the repository is scoped, this is not.
/// </summary>
public sealed class EveSettingsPreferences(IServiceProvider services) : ISingletonService
{
    private const string InstallRootKey = "eve-settings.install-root";
    private const string AccountNamePrefix = "eve-settings.account-name.";
    private const string AccountLinksKey = "eve-settings.account-links";
    private const string AutoSyncKey = "eve-settings.auto-sync";
    private const string AutoSyncHistoryKey = "eve-settings.auto-sync.history";

    /// <summary>How many automatic runs are kept. Enough to see a pattern, few enough to stay a settings value.</summary>
    private const int HistoryLength = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    // The automatic sync writes its history from a background loop while the tool may be saving from the UI thread;
    // an append is read-modify-write, so it is serialised here rather than left to chance.
    private readonly SemaphoreSlim _historyGate = new(1, 1);

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

    // ── Which characters sit on which account (ET-64) ────────────────────────────────────────────

    /// <summary>Every account link established so far, by account id. Empty on a first run.</summary>
    public async Task<IReadOnlyDictionary<long, AccountCharacterLink>> LoadAccountLinksAsync(
        CancellationToken cancellationToken = default)
    {
        var links = _Read<List<AccountCharacterLink>>(await _ValueAsync(AccountLinksKey, cancellationToken));
        return links is null
            ? new Dictionary<long, AccountCharacterLink>()
            : links.GroupBy(link => link.AccountId).ToDictionary(group => group.Key, group => group.Last());
    }

    public Task SaveAccountLinksAsync(IEnumerable<AccountCharacterLink> links, CancellationToken cancellationToken = default) =>
        _UpsertAsync(AccountLinksKey,
            JsonSerializer.Serialize(links.OrderBy(link => link.AccountId).ToList(), JsonOptions), cancellationToken);

    // ── The automatic sync (ET-60) ───────────────────────────────────────────────────────────────

    /// <summary>The standing instruction, or <see cref="AutoSyncSettings.None"/> when there is none — a corrupt or
    /// half-written value reads as "nothing configured" rather than as an instruction to overwrite files.</summary>
    public async Task<AutoSyncSettings> LoadAutoSyncAsync(CancellationToken cancellationToken = default) =>
        _Read<AutoSyncSettings>(await _ValueAsync(AutoSyncKey, cancellationToken)) ?? AutoSyncSettings.None;

    public Task SaveAutoSyncAsync(AutoSyncSettings settings, CancellationToken cancellationToken = default) =>
        _UpsertAsync(AutoSyncKey, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);

    /// <summary>What the automatic sync has done, newest first.</summary>
    public async Task<IReadOnlyList<AutoSyncRun>> LoadAutoSyncHistoryAsync(CancellationToken cancellationToken = default)
    {
        var runs = _Read<List<AutoSyncRun>>(await _ValueAsync(AutoSyncHistoryKey, cancellationToken));
        return runs is null ? [] : runs.OrderByDescending(run => run.AtUtc).ToList();
    }

    /// <summary>Records one run and drops the oldest beyond <see cref="HistoryLength"/>.</summary>
    public async Task AppendAutoSyncRunAsync(AutoSyncRun run, CancellationToken cancellationToken = default)
    {
        await _historyGate.WaitAsync(cancellationToken);
        try
        {
            var history = (await LoadAutoSyncHistoryAsync(cancellationToken)).ToList();
            history.Insert(0, run);
            await _UpsertAsync(AutoSyncHistoryKey,
                JsonSerializer.Serialize(history.Take(HistoryLength).ToList(), JsonOptions), cancellationToken);
        }
        finally
        {
            _historyGate.Release();
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private static T? _Read<T>(string? value) where T : class
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonOptions);
        }
        catch (JsonException)
        {
            return null;   // a value we cannot read is not an instruction we should act on
        }
    }

    private async Task<string?> _ValueAsync(string key, CancellationToken cancellationToken) =>
        (await _ListAsync(cancellationToken)).FirstOrDefault(setting => setting.Key == key)?.Value;

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
