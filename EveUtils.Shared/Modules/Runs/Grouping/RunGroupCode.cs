using System.Security.Cryptography;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Grouping;

public static class RunGroupCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Create(ActivityKind activityKind)
    {
        Span<char> suffix = stackalloc char[4];
        for (int index = 0; index < suffix.Length; index++)
            suffix[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        string prefix = activityKind == ActivityKind.Site ? "HF" : "AB";
        return $"{prefix}-{new string(suffix)}";
    }
}
