using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// Admin seed-password resolution (A4): a configured value is always used; outside Development a missing value is a
/// fail-fast config error (never the known "admin" default in Production); in Development a missing value falls back to
/// "admin" for local convenience.
/// </summary>
public class AdminSeedPasswordTests
{
    [Fact]
    public void Resolve_NullOutsideDevelopment_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => AdminSeedPassword.Resolve(null, isDevelopment: false));
    }

    [Fact]
    public void Resolve_NullInDevelopment_FallsBackToAdmin()
    {
        Assert.Equal("admin", AdminSeedPassword.Resolve(null, isDevelopment: true));
    }

    [Fact]
    public void Resolve_ConfiguredValue_IsUsed_OutsideDevelopment()
    {
        Assert.Equal("s3cret", AdminSeedPassword.Resolve("s3cret", isDevelopment: false));
    }

    [Fact]
    public void Resolve_ConfiguredValue_IsUsed_InDevelopment()
    {
        Assert.Equal("s3cret", AdminSeedPassword.Resolve("s3cret", isDevelopment: true));
    }
}
