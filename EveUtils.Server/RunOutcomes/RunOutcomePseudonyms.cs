using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EveUtils.Server.RunOutcomes;

/// <summary>
/// The keyed pseudonyms a shared run outcome is stored under (ET-514): HMAC-SHA256 with a random 256-bit key of this
/// server's own. A plain hash would not do: character ids are a small, public number space, so anyone holding the
/// outcome table could hash every id and read the sharers back. The key lives in the data directory rather than the
/// database, so a copy of the database alone names nobody; it is in the backup archive, so a restored server still
/// recognises its pilots — the per-pilot cap keeps counting them as one, and an opt-out still finds every outcome.
/// It is created once and never rotated: a new key would count every pilot as a new one.
/// </summary>
public sealed class RunOutcomePseudonyms
{
    public const string KeyFileName = "run-outcomes.key";

    private readonly byte[] _key;

    public RunOutcomePseudonyms(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        string path = Path.Combine(dataDirectory, KeyFileName);
        if (File.Exists(path))
        {
            _key = File.ReadAllBytes(path);
            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, _key);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Who shared an outcome, as far as the per-pilot cap and the sharer's own retract and withdraw go.</summary>
    public string ContributorBucket(long characterId) =>
        _Hash("contributor:" + characterId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Which activity an outcome is of: the same for every fleet mate who flew it, unlinkable to its group
    /// code without the key.</summary>
    public string ActivityId(string activityReference) => _Hash("activity:" + activityReference);

    private string _Hash(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));
}
