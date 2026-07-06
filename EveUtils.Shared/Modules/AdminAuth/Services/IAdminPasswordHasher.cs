namespace EveUtils.Shared.Modules.AdminAuth.Services;

/// <summary>Hashes + verifies admin passwords (PBKDF2, framework crypto — no NuGet, no SHA256).</summary>
public interface IAdminPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}
