using System.Security.Cryptography;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.ServerAuth.Services;

namespace EveUtils.Shared.Modules.Market.Services.Implementations;

/// <summary>See <see cref="IEveWorkbenchKeyStore"/>. Same AES-256-GCM blob layout (nonce | tag | cipher) as
/// <c>EncryptedPerCharacterTokenStore</c> uses for ESI tokens, base64-encoded so it fits one Settings string value.
/// The protector's own key file never leaves the machine, so the setting value alone decrypts nothing.</summary>
public sealed class EveWorkbenchKeyStore(IDispatcher dispatcher, ITokenProtector protector) : IEveWorkbenchKeyStore
{
    public const string SettingKey = "appraisal.eveworkbench.token";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dispatcher.Query(new GetSettingsQuery(), cancellationToken);
        var stored = settings.FirstOrDefault(setting => setting.Key == SettingKey)?.Value;
        if (string.IsNullOrEmpty(stored))
            return null;

        try
        {
            return protector.Unprotect(_Decode(stored));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // A corrupt or foreign value is treated as "no token", not a fatal error — matches
            // EncryptedPerCharacterTokenStore's stance on a tag mismatch.
            return null;
        }
    }

    public async Task SetTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await dispatcher.Send(new DeleteSettingCommand(SettingKey), cancellationToken);
            return;
        }

        await dispatcher.Send(new SetSettingCommand(SettingKey, _Encode(protector.Protect(token))), cancellationToken);
    }

    private static string _Encode(EncryptedToken token)
    {
        var blob = new byte[token.Nonce.Length + token.Tag.Length + token.Cipher.Length];
        Buffer.BlockCopy(token.Nonce, 0, blob, 0, token.Nonce.Length);
        Buffer.BlockCopy(token.Tag, 0, blob, token.Nonce.Length, token.Tag.Length);
        Buffer.BlockCopy(token.Cipher, 0, blob, token.Nonce.Length + token.Tag.Length, token.Cipher.Length);
        return Convert.ToBase64String(blob);
    }

    private static EncryptedToken _Decode(string encoded)
    {
        var blob = Convert.FromBase64String(encoded);
        if (blob.Length < NonceSize + TagSize)
            throw new FormatException("The stored EVE Workbench token blob is too short.");

        return new EncryptedToken(
            Cipher: blob[(NonceSize + TagSize)..],
            Nonce: blob[..NonceSize],
            Tag: blob[NonceSize..(NonceSize + TagSize)]);
    }
}
