using System.Security.Cryptography;
using System.Text;

namespace EveUtils.Shared.Modules.ServerAuth.Services;

/// <summary>Opaque server-session tokens: random generation + one-way hashing for storage.</summary>
public static class TokenSecurity
{
    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
