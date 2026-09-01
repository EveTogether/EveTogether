using System.Security.Cryptography;
namespace EveUtils.Shared.Modules.Runs.Grouping;

public static class RunGroupCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Create()
    {
        Span<char> suffix = stackalloc char[4];
        for (int index = 0; index < suffix.Length; index++)
            suffix[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return $"HF-{new string(suffix)}";
    }
}
