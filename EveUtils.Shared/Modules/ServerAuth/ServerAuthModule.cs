using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Modules.ServerAuth.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.ServerAuth;

/// <summary>
/// Server-only auth module (Mode B): the allowed-list, paired characters (encrypted refresh tokens)
/// and server sessions. Entity-owning, so it lives in Shared but is only loaded by the server
/// context — the tables land in the server DB.
/// </summary>
public static class ServerAuthModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SyncedCharacterConfiguration());
        modelBuilder.ApplyConfiguration(new ServerSessionConfiguration());
        modelBuilder.ApplyConfiguration(new AllowedCharacterConfiguration());
    }

    // The repository is auto-registered by AddSharedServices; this only adds the token protector,
    // which needs the data directory (a factory binding that can't be a plain marker).
    public static IServiceCollection AddServerAuthModule(this IServiceCollection services, string dataDirectory)
    {
        // Constructed eagerly, so the key file exists long before the database is reachable. Its created-or-loaded
        // verdict is published separately for the startup guard to pick up once it is (ET-94).
        var protector = new AesGcmTokenProtector(dataDirectory);
        services.AddSingleton<ITokenProtector>(protector);
        services.AddSingleton(new TokenProtectorKeyState(protector.KeyWasCreated, protector.KeyPath));
        return services;
    }
}
