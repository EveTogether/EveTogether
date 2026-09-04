using EveUtils.Server.Backup;
using EveUtils.Server.Data;
using EveUtils.Server.Esi;
using EveUtils.Server.Grpc;
using EveUtils.Server.Permissions;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Logging;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.AdminAuth;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.ServerAuth;
using EveUtils.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-180: the server registered eighteen Runs handlers it could never construct — every one of them asks for the
/// client database — and died on the first line of startup. It stayed unnoticed for three days because nothing
/// ever built the server's container outside a real launch, and a real launch needs a registered EVE application
/// and a machine's data directory. So the container is built here instead, from services alone: no Kestrel, no
/// certificate, no configuration anyone has to own.
/// </summary>
public class ServerCompositionTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "eveutils-composition-" + Guid.NewGuid().ToString("N"));

    public ServerCompositionTests() => Directory.CreateDirectory(_dataDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    /// <summary>
    /// The guard rail. Everything the server registers must be resolvable from what the server registers —
    /// the very check the runtime runs at <c>builder.Build()</c>, here without a host.
    ///
    /// It catches any handler the scan picks up but cannot build, not only the Runs ones: that is the point.
    /// A failure names the service and the dependency it went looking for.
    /// </summary>
    [Fact]
    public void EverythingTheServerRegisters_CanAlsoBeConstructed()
    {
        ServiceCollection services = _ComposeServer();

        try
        {
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true })
                .Dispose();
        }
        catch (AggregateException failures)
        {
            Assert.Fail("The server registers services it cannot construct:\n\n"
                        + string.Join("\n\n", failures.InnerExceptions.Select(failure => failure.Message)));
        }
    }

    /// <summary>
    /// The same fault stated as the rule it broke, so a reader gets the reason and not just a stack trace: the
    /// server has no client database, so nothing it composes may ask for one. This reads only what the scan
    /// itself claims, so it keeps standing even if <see cref="_ComposeServer"/> ever drifts from Program.cs.
    /// </summary>
    [Fact]
    public void NothingTheServerScanRegisters_AsksForTheClientDatabase()
    {
        var services = new ServiceCollection();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddAutoServices(typeof(Program).Assembly, ExecutionHost.Server);

        string[] offenders = services
            .Select(descriptor => descriptor.ImplementationType)
            .OfType<Type>()
            .Distinct()
            .SelectMany(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => _WantsTheClientDatabase(parameter.ParameterType))
                .Select(parameter => type.FullName + " (" + parameter.Name + ")"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These are registered on the server but need the client database, which the server does not have. "
            + "Mark them [ClientOnly] so the server scan skips them:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The client side of the same coin: skipping types on the server must not cost the client any of them.
    /// The Runs handlers are client-only, and the client is where they do their work.
    /// </summary>
    [Fact]
    public void TheClientScan_StillRegistersEveryClientOnlyType()
    {
        var client = new ServiceCollection();
        client.AddSharedServices(ExecutionHost.Client);

        var server = new ServiceCollection();
        server.AddSharedServices(ExecutionHost.Server);

        Type[] clientOnly = typeof(ClientDbContext).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(ClientOnlyAttribute), inherit: false))
            .ToArray();

        Assert.NotEmpty(clientOnly);
        Assert.All(clientOnly, type =>
        {
            Assert.Contains(client, descriptor => descriptor.ImplementationType == type);
            Assert.DoesNotContain(server, descriptor => descriptor.ImplementationType == type);
        });
    }

    /// <summary>A dependency on the client database, however it is spelled: the context itself, or a factory,
    /// provider or wrapper closed over it.</summary>
    private static bool _WantsTheClientDatabase(Type type) =>
        type == typeof(ClientDbContext)
        || (type.IsGenericType && type.GetGenericArguments().Any(argument => argument == typeof(ClientDbContext)));

    /// <summary>
    /// The server's composition minus its host: the scans plus every module registration that something the scans
    /// pick up can depend on, in Program.cs order, with the parts that need a machine (the real data directory, a
    /// registered EVE application) replaced by throwaway stand-ins. The ESI values below are placeholders, and
    /// nothing here reaches the network — validation resolves call sites, it does not run them.
    ///
    /// Deliberately short of Program.cs on the web host itself — Kestrel, gRPC, SignalR, the cookie/API-key auth,
    /// the OpenAPI document and the hosted services. Nothing the scans register depends on those, so leaving them
    /// out costs this check nothing; it does mean a hosted service that cannot be constructed is still only caught
    /// by a real launch.
    ///
    /// So this is a partial mirror, and it can fall behind. If it fails on a dependency Program.cs plainly
    /// registers, add the missing line here rather than loosening the check.
    /// </summary>
    private ServiceCollection _ComposeServer()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:Sqlite"] = "Data Source=:memory:",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddAppLogStore(_dataDirectory);
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddSingleton<IAccessPolicy, ToggleablePolicy>();
        services.AddCqrs();
        services.AddEventBus();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddAutoServices(typeof(Program).Assembly, ExecutionHost.Server);
        services.AddServerDatabase(configuration, _dataDirectory);
        services.AddSingleton(new EsiOptions { ClientId = "composition-test", ClientSecret = "composition-test" });
        services.AddModuleEsiScopes(new ServerOptionalScopeCatalog());
        services.AddEsiScopeRegistry();
        services.AddEsiPipeline(_dataDirectory);
        services.AddSingleton(new ServerInfo("Composition test"));
        services.AddServerAuthModule(_dataDirectory);
        services.AddSingleton(new ServerBackupOptions(_dataDirectory));
        services.AddAdminAuthModule();
        services.AddFittingsServerModule();
        services.AddFleetModule();
        services.AddMessagingModule();
        services.AddSdeModule(_dataDirectory);
        services.AddWireEvents();
        services.AddSingleton<ConnectedClients>();
        return services;
    }
}
