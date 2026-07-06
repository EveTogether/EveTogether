using EveUtils.Client.Composition;
using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Builds the real client DI on a unique throwaway <c>EVEUTILS_INSTANCE</c> so a UI test never touches a real
/// client database (feedback_test_setup_isolation: an instance name is no guarantee of emptiness — always use a
/// guaranteed-unique scratch instance). Everything runs locally: no server, no gRPC. Disposing tears the service
/// provider down, clears the env var and deletes the scratch data directory.
/// </summary>
public sealed class TestClientInstance : IDisposable
{
    private readonly string _dataDirectory;

    private TestClientInstance(IServiceProvider services, string dataDirectory)
    {
        Services = services;
        _dataDirectory = dataDirectory;
    }

    public IServiceProvider Services { get; }

    /// <param name="configure">Optional override hook passed through to <see cref="ClientServices.Build"/> — lets a
    /// test substitute a fake registration (e.g. <c>IFleetTransportClient</c> / <c>IDialogService</c>) before the
    /// provider is built, so a server-bound view-model can be driven without a running server.</param>
    public static TestClientInstance Create(Action<IServiceCollection>? configure = null)
    {
        var name = "uitest-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("EVEUTILS_INSTANCE", name);

        var services = ClientServices.Build(configure);

        // ClientServices.Build() does not migrate (Program.Main does); apply the client migration stack here.
        using (var scope = services.CreateScope())
        {
            using var db = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<ClientDbContext>>()
                .CreateDbContext();
            db.Database.Migrate();
        }

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveUtils", name);
        return new TestClientInstance(services, dataDirectory);
    }

    public void Dispose()
    {
        (Services as IDisposable)?.Dispose();
        Environment.SetEnvironmentVariable("EVEUTILS_INSTANCE", null);

        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of scratch data; a leftover throwaway dir is harmless.
        }
    }
}
