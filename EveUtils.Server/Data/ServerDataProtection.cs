using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server.Data;

internal static class ServerDataProtection
{
    public static void Configure(IServiceCollection services, string dataDirectory)
    {
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "data-protection-keys")))
            .SetApplicationName("EveTogether.Server");
    }
}
