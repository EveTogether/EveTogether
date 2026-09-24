using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class TestDataRootCleanupTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void CleanOrphans_AgeAndLock_OnlyRemovesOldUnlockedRoots(bool old, bool locked, bool expectedExists)
    {
        string parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "et359-cleanup-" + Guid.NewGuid().ToString("N"));
        string candidate = System.IO.Path.Combine(parent, "eveutils-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(candidate);
        string lockPath = System.IO.Path.Combine(candidate, ".lock");
        File.WriteAllText(lockPath, string.Empty);

        try
        {
            using (Stream heldLock = locked
                       ? new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                       : Stream.Null)
            {
                Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow - (old ? TimeSpan.FromHours(2) : TimeSpan.Zero));
                TestDataRoot.CleanOrphans(parent);
                Assert.Equal(expectedExists, Directory.Exists(candidate));
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
