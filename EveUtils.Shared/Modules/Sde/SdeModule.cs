using EveUtils.Shared.App;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using EveUtils.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Sde;

/// <summary>
/// Registration of the read-only SDE module. Anchors the store + work dir to the host's data directory, registers
/// the accessor, and configures the named "sde" download client (central User-Agent). The source + importer carry
/// lifetime markers and are auto-registered by <c>AddSharedServices</c>; each host triggers the import
/// itself (server: silent hosted service; client: user-prompted with a progress popup).
/// </summary>
public static class SdeModule
{
    public static IServiceCollection AddSdeModule(this IServiceCollection services, string dataDirectory)
    {
        var sdeDirectory = Path.Combine(dataDirectory, "sde");
        var options = new SdeOptions
        {
            DatabasePath = Path.Combine(sdeDirectory, "sde.sqlite"),
            WorkDirectory = Path.Combine(sdeDirectory, "work")
        };
        services.AddSingleton(options);
        services.AddSingleton<ISdeAccessor>(_ => new SqliteSdeAccessor(options.DatabasePath));
        // Separate dogma-calc seam over the same store/pool; the Dogma engine consumes this, the parsers ISdeAccessor.
        services.AddSingleton<IDogmaDataAccessor>(_ => new SqliteDogmaDataAccessor(options.DatabasePath));

        services.AddHttpClient(SdeEndpoints.HttpClientName, (sp, client) =>
        {
            // One-time ~80 MB download; let cancellation bound it instead of a wall-clock timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
            var runtime = sp.GetRequiredService<IRuntimeContext>();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent(runtime.Host));
        });

        return services;
    }
}
