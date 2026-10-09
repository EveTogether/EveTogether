using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EveUtils.Server.Data;
using Xunit;

namespace EveUtils.Server.Tests;

public sealed class DataProtectionPersistenceTests
{
    [Fact]
    public void Configure_SameDataDirectoryAcrossHosts_UnprotectsPayload()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            using IHost firstHost = _CreateHost(dataDirectory);
            var firstProvider = firstHost.Services.GetRequiredService<IDataProtectionProvider>();
            var protectedPayload = firstProvider.CreateProtector("control-panel").Protect("session payload");

            using IHost secondHost = _CreateHost(dataDirectory);
            var secondProvider = secondHost.Services.GetRequiredService<IDataProtectionProvider>();

            Assert.Equal("session payload", secondProvider.CreateProtector("control-panel").Unprotect(protectedPayload));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static IHost _CreateHost(string dataDirectory)
    {
        var builder = Host.CreateApplicationBuilder();
        ServerDataProtection.Configure(builder.Services, dataDirectory);
        return builder.Build();
    }
}
