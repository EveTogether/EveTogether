using EveUtils.Server.Backup;
using EveUtils.Server.RunOutcomes;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-514: the per-pilot cap needs to know which outcomes are one pilot's without the server keeping who. The key has to
/// outlive a restart — a new one per start would count every pilot again as a new one — and has to travel with a backup,
/// or a pilot's opt-out on a restored server would no longer find what they shared.
/// </summary>
public sealed class RunOutcomePseudonymsTests : IDisposable
{
    private const long Jithran = 90250177;

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "et514-key-" + Guid.NewGuid().ToString("N"));
    private readonly string _otherDirectory = Path.Combine(Path.GetTempPath(), "et514-key-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (string directory in new[] { _dataDirectory, _otherDirectory }.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void ContributorBucket_AfterARestart_IsTheSame()
    {
        string before = new RunOutcomePseudonyms(_dataDirectory).ContributorBucket(Jithran);

        string after = new RunOutcomePseudonyms(_dataDirectory).ContributorBucket(Jithran);

        Assert.Equal(before, after);
        Assert.True(File.Exists(Path.Combine(_dataDirectory, RunOutcomePseudonyms.KeyFileName)));
    }

    [Fact]
    public void ContributorBucket_AnotherServersKey_GivesAnotherBucket()
    {
        Assert.NotEqual(new RunOutcomePseudonyms(_dataDirectory).ContributorBucket(Jithran),
            new RunOutcomePseudonyms(_otherDirectory).ContributorBucket(Jithran));
    }

    [Fact]
    public void Pseudonyms_AreKeyedHashes_NotTheIdOrAPlainHashOfIt()
    {
        RunOutcomePseudonyms pseudonyms = new(_dataDirectory);

        string bucket = pseudonyms.ContributorBucket(Jithran);

        Assert.Matches("^[0-9a-f]{64}$", bucket);
        Assert.DoesNotContain(Jithran.ToString(), bucket);
        Assert.NotEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("contributor:90250177"u8)), bucket);
        Assert.NotEqual(pseudonyms.ContributorBucket(Jithran + 1), bucket);
        Assert.Equal(pseudonyms.ActivityId("FLEET-AB12"), pseudonyms.ActivityId("FLEET-AB12"));
        Assert.NotEqual(pseudonyms.ActivityId("FLEET-AB12"), pseudonyms.ActivityId("FLEET-AB13"));
    }

    [Fact]
    public void Backup_CarriesTheKey()
    {
        Assert.Contains(RunOutcomePseudonyms.KeyFileName, ServerBackupOptions.ArchivedFiles);
    }
}
