namespace EveUtils.Shared.Cqrs.Permissions;

/// <summary>A module's permission declarations, registered via <c>AddModulePermissions</c>.</summary>
public interface IPermissionCatalog
{
    IEnumerable<PermissionDescriptor> Descriptors { get; }
}
