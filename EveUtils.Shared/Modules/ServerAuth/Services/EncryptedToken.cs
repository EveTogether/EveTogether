namespace EveUtils.Shared.Modules.ServerAuth.Services;

/// <summary>AES-256-GCM ciphertext parts for a stored token.</summary>
public sealed record EncryptedToken(byte[] Cipher, byte[] Nonce, byte[] Tag);
