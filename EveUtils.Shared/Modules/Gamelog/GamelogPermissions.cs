using EveUtils.Shared.Cqrs.Permissions;

namespace EveUtils.Shared.Modules.Gamelog;

/// <summary>The Gamelog module's app-permissions, registered via <c>AddModulePermissions</c>.</summary>
public sealed class GamelogPermissions : IPermissionCatalog
{
    public const string View = "gamelog.view";
    public const string Stream = "gamelog.stream";
    public const string Record = "gamelog.record";

    public IEnumerable<PermissionDescriptor> Descriptors { get; } =
    [
        new(View, "View gamelog", "Read persisted combat samples.", "Gamelog"),
        new(Stream, "Stream gamelog", "Stream live DPS over the remote bus.", "Gamelog"),
        new(Record, "Record gamelog", "Persist a combat sample.", "Gamelog")
    ];
}
