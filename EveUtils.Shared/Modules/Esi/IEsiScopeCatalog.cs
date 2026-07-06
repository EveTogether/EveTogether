namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Module-level declaration of required ESI scopes. Implement per module and register
/// via <c>services.AddModuleEsiScopes(new XxxScopeCatalog())</c> inside <c>AddXxxModule()</c>.
/// Mirrors the <see cref="EveUtils.Shared.Cqrs.Permissions.IPermissionCatalog"/> pattern.
/// </summary>
public interface IEsiScopeCatalog
{
    IReadOnlyList<EsiScopeRequirement> Requirements { get; }
}
