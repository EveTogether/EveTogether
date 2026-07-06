namespace EveUtils.Shared.Modules.ServerAuth.Services;

/// <summary>Encrypts/decrypts EVE refresh tokens at rest on the server.</summary>
public interface ITokenProtector
{
    EncryptedToken Protect(string plaintext);

    string Unprotect(EncryptedToken token);
}
