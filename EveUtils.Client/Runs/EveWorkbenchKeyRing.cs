using System.Text.Json;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Services.Implementations;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Market.Services.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Runs;

/// <summary>
/// The EVE Workbench API keys the user entered (ET-325), one per EVE Workbench account. A key is stored once, with the
/// characters EVE Workbench says belong to that account, so every pilot on that list uses it without entering anything.
/// Secrets are stored encrypted the way <see cref="EveWorkbenchKeyStore"/> does.
/// </summary>
public sealed class EveWorkbenchKeyRing(ISettingRepository settings, ITokenProtector protector, IHttpClientFactory httpClientFactory)
    : ISingletonService
{
    public const string SettingKey = "runs.ewb-publish.keys";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The key a pilot uses: its own (it is the account's main) before one it rides along on.</summary>
    public async Task<ResolvedEveWorkbenchKey?> ForPilotAsync(long characterId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoredKey> keys = await _LoadAsync(cancellationToken);
        StoredKey? own = keys.FirstOrDefault(key => key.MainId == characterId);
        StoredKey? found = own ?? keys.FirstOrDefault(key => key.Characters.Any(character => character.Id == characterId));
        return found is null ? null : _Resolve(found, isOwn: own is not null);
    }

    /// <summary>The first key still accepted; what appraisal falls back to.</summary>
    public async Task<string?> FirstValidTokenAsync(CancellationToken cancellationToken = default) =>
        (await _LoadAsync(cancellationToken)).Where(key => !key.Invalid).Select(key => _Unprotect(key.Secret)).FirstOrDefault();

    /// <summary>Checks <paramref name="token"/> with EVE Workbench and stores it, with its characters, when accepted.</summary>
    public async Task<EveWorkbenchKeyCheck> AddAsync(string token, CancellationToken cancellationToken = default)
    {
        string trimmed = token.Trim();
        EveWorkbenchKeyCheck check = await CheckAsync(trimmed, cancellationToken);
        if (check.Verdict == EveWorkbenchKeyVerdict.Valid && check.Characters.Count > 0)
        {
            // Implicit contract: v1/characters lists the key's own (main) character first, then its toons.
            await _UpdateAsync(keys =>
            {
                keys.RemoveAll(key => key.MainId == check.Characters[0].Id);
                keys.Add(new StoredKey(check.Characters[0].Id, check.Characters[0].Name, [.. check.Characters], _Protect(trimmed), false));
            }, cancellationToken);
        }

        return check;
    }

    public Task RemoveAsync(long mainId, CancellationToken cancellationToken = default) =>
        _UpdateAsync(keys => keys.RemoveAll(key => key.MainId == mainId), cancellationToken);

    public Task MarkInvalidAsync(long mainId, CancellationToken cancellationToken = default) =>
        _UpdateAsync(keys =>
        {
            int index = keys.FindIndex(key => key.MainId == mainId);
            if (index >= 0)
            {
                keys[index] = keys[index] with { Invalid = true };
            }
        }, cancellationToken);

    /// <summary>Asks EVE Workbench again who each key covers: a character that left the account falls back to no key,
    /// a refused key is marked invalid. An unreachable EVE Workbench changes nothing.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (StoredKey key in await _LoadAsync(cancellationToken))
        {
            EveWorkbenchKeyCheck check = await CheckAsync(_Unprotect(key.Secret), cancellationToken);
            if (check.Verdict == EveWorkbenchKeyVerdict.Invalid)
            {
                await MarkInvalidAsync(key.MainId, cancellationToken);
            }
            else if (check.Verdict == EveWorkbenchKeyVerdict.Valid && check.Characters.Count > 0)
            {
                await _UpdateAsync(keys =>
                {
                    keys.RemoveAll(stored => stored.MainId == key.MainId);
                    keys.Add(key with { MainId = check.Characters[0].Id, MainName = check.Characters[0].Name, Characters = [.. check.Characters], Invalid = false });
                }, cancellationToken);
            }
        }
    }

    /// <summary>The single key of an earlier version, once, as a key of the account it turns out to belong to.</summary>
    public async Task MigrateLegacyAsync(string? legacyToken, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(legacyToken) && (await _LoadAsync(cancellationToken)).Count == 0)
        {
            await AddAsync(legacyToken, cancellationToken);
        }
    }

    public async Task<EveWorkbenchKeyCheck> CheckAsync(string token, CancellationToken cancellationToken = default)
    {
        string baseUrl = (await settings.ListAsync(cancellationToken))
            .FirstOrDefault(setting => setting.Key == EveWorkbenchRunAutoPublisher.UrlSettingKey)?.Value ?? EveWorkbenchFitClient.BaseUrl;
        var publisher = new EveWorkbenchRunPublisher(httpClientFactory.CreateClient(EveWorkbenchRunPublisher.HttpClientName));
        return await publisher.CheckKeyAsync(baseUrl, token, cancellationToken);
    }

    private ResolvedEveWorkbenchKey _Resolve(StoredKey key, bool isOwn) =>
        new(key.MainId, key.MainName, _Unprotect(key.Secret), key.Invalid, isOwn, key.Characters);

    private string _Protect(string token) => EveWorkbenchKeyStore.Encode(protector.Protect(token));

    private string _Unprotect(string secret) => protector.Unprotect(EveWorkbenchKeyStore.Decode(secret));

    private async Task<IReadOnlyList<StoredKey>> _LoadAsync(CancellationToken cancellationToken)
    {
        string? json = (await settings.ListAsync(cancellationToken)).FirstOrDefault(setting => setting.Key == SettingKey)?.Value;
        return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<StoredKey>>(json) ?? [];
    }

    private async Task _UpdateAsync(Action<List<StoredKey>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<StoredKey> keys = [.. await _LoadAsync(cancellationToken)];
            change(keys);
            await settings.UpsertAsync(SettingKey, JsonSerializer.Serialize(keys), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record StoredKey(long MainId, string MainName, List<EveWorkbenchCharacter> Characters, string Secret, bool Invalid);
}

/// <summary>What a pilot publishes with; <see cref="IsOwn"/> when the pilot is the account's main, else it rides along.</summary>
public sealed record ResolvedEveWorkbenchKey(long MainId, string MainName, string Token, bool Invalid, bool IsOwn,
    IReadOnlyList<EveWorkbenchCharacter> Characters);

/// <summary>Appraisal's key: the first key of the ring still accepted, else the single key it always had.</summary>
public sealed class EveWorkbenchKeyStoreWithRing(EveWorkbenchKeyStore legacy, EveWorkbenchKeyRing ring) : IEveWorkbenchKeyStore
{
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        await ring.FirstValidTokenAsync(cancellationToken) ?? await legacy.GetTokenAsync(cancellationToken);

    public Task SetTokenAsync(string? token, CancellationToken cancellationToken = default) => legacy.SetTokenAsync(token, cancellationToken);
}
