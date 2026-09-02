using EveUtils.Shared.Modules.ApiKeys.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.ApiKeys;

/// <summary>
/// Server-only API-key module: the keys that gate the read-only REST API under <c>/api/v1</c>. Entity-owning,
/// so it lives in Shared but is only loaded by the server context. Repository and handlers auto-register
/// via their markers; the panel permission is declared by the AdminAuth catalog.
/// </summary>
public static class ApiKeysModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new ApiKeyConfiguration());
}
