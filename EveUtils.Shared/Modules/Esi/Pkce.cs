using System.Security.Cryptography;
using System.Text;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>PKCE pair (S256). The verifier never leaves the client; only the challenge is sent.</summary>
public sealed record Pkce(string Verifier, string Challenge)
{
    public const string Method = "S256";

    public static Pkce Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new Pkce(verifier, challenge);
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
