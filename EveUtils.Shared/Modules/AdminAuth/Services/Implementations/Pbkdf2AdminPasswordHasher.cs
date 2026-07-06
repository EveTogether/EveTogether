using System.Security.Cryptography;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.AdminAuth.Services;

namespace EveUtils.Shared.Modules.AdminAuth.Services.Implementations;

/// <summary>
/// PBKDF2-SHA256 password hasher using only the BCL (<see cref="Rfc2898DeriveBytes"/>) — no NuGet,
/// no ASP.NET dependency in Shared. Self-describing format keeps stored hashes upgradeable:
/// <c>pbkdf2$sha256$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;subkeyB64&gt;</c>.
/// </summary>
internal sealed class Pbkdf2AdminPasswordHasher : IAdminPasswordHasher, ISingletonService
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int SubkeyBytes = 32;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, SubkeyBytes);
        return $"pbkdf2$sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(subkey)}";
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
            return false;

        var parts = hash.Split('$');
        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256")
            return false;
        if (!int.TryParse(parts[2], out var iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
