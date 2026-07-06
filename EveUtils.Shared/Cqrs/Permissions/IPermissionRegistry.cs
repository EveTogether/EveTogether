namespace EveUtils.Shared.Cqrs.Permissions;

/// <summary>
/// Code-derived registry of all module permissions — rebuilt at startup from the registered
/// <see cref="IPermissionCatalog"/>s, never stored.
/// </summary>
public interface IPermissionRegistry
{
    bool Contains(string code);
    IReadOnlyCollection<PermissionDescriptor> All();
}
