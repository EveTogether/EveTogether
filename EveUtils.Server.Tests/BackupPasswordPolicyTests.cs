using EveUtils.Server.Backup;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The length rule and the generator behind it (ET-102). The ZIP format derives its key with 1000 PBKDF2 rounds
/// and nothing here can raise that, so the password is the only thing standing between a stolen archive and every
/// refresh token in it — which makes both of these load-bearing rather than cosmetic.
/// </summary>
public class BackupPasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nineteen-chars-long")]
    public void IsValid_TooShort_IsRefused(string? password) =>
        Assert.False(BackupPasswordPolicy.IsValid(password));

    [Fact]
    public void IsValid_AtTheMinimum_IsAccepted() =>
        Assert.True(BackupPasswordPolicy.IsValid(new string('x', BackupPasswordPolicy.MinLength)));

    [Fact]
    public void Generate_Always_MeetsThePolicyItIsOfferedFor() =>
        Assert.True(BackupPasswordPolicy.IsValid(BackupPasswordPolicy.Generate()));

    /// <summary>A generator that returns the same thing twice would be worse than no generator: the admin would
    /// have no way of telling, and every archive on every server would share one password.</summary>
    [Fact]
    public void Generate_CalledRepeatedly_ReturnsADifferentPasswordEveryTime()
    {
        var generated = Enumerable.Range(0, 50).Select(_ => BackupPasswordPolicy.Generate()).ToHashSet();

        Assert.Equal(50, generated.Count);
    }
}
