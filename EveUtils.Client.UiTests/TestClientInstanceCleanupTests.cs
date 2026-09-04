using System;
using System.IO;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-181: 153k+ scratch directories piled up because Dispose() silently failed to delete them. Covers the
/// two acceptance criteria that matter — a passing test and a failing one both leave nothing behind — plus the
/// boundary that cleanup never reaches outside its own directory.</summary>
public class TestClientInstanceCleanupTests
{
    [Fact]
    public void Dispose_OnASucceedingTest_LeavesNoDataDirectoryBehind()
    {
        var instance = TestClientInstance.Create();
        var dataDirectory = instance.DataDirectory;
        Assert.True(Directory.Exists(dataDirectory));

        instance.Dispose();

        Assert.False(Directory.Exists(dataDirectory));
    }

    [Fact]
    public void ATestThatThrowsInsideItsInstance_OnlyCleansUpItsOwnDirectory()
    {
        var instance = TestClientInstance.Create();
        var siblingDirectory = Path.Combine(
            Path.GetDirectoryName(instance.DataDirectory)!, "not-a-uitest-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(siblingDirectory);

        try
        {
            var threw = false;
            try
            {
                throw new InvalidOperationException("simulated test failure");
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            finally
            {
                instance.Dispose(); // mirrors what `using var instance = TestClientInstance.Create()` compiles to
            }
            Assert.True(threw);

            // ET-181 criterion 2: the instance's own directory is gone even though the test failed
            Assert.False(Directory.Exists(instance.DataDirectory));
            // ET-181 criterion 3: cleanup never touches a sibling directory outside its own name
            Assert.True(Directory.Exists(siblingDirectory));
        }
        finally
        {
            if (Directory.Exists(siblingDirectory))
                Directory.Delete(siblingDirectory, recursive: true);
        }
    }
}
