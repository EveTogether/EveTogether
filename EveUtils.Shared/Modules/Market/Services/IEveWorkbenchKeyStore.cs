namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>
/// Reads and writes the EVE Workbench personal access token (ET-364). The token is a secret: it is encrypted at
/// rest, stored as one Settings value through <c>SetSettingCommand</c> so it survives a restart without a dedicated
/// table, and never appears in a log line or an exception message.
/// </summary>
public interface IEveWorkbenchKeyStore
{
    /// <summary>The decrypted token, or null when none is configured or the stored value cannot be decrypted.</summary>
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Encrypts and stores <paramref name="token"/>; a null or blank value deletes the setting instead.</summary>
    Task SetTokenAsync(string? token, CancellationToken cancellationToken = default);
}
