using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>SDE-backed <see cref="ISdeNameResolver"/>: resolves type ids via the read-only SDE store, falling back to
/// <c>type {id}</c> when the store is unavailable (not imported yet) or has no name for the id.</summary>
public sealed class SdeNameResolver : ISdeNameResolver
{
    private readonly ISdeAccessor _sde;

    public SdeNameResolver(ISdeAccessor sde) => _sde = sde;

    public string TypeName(int typeId) =>
        _sde.IsAvailable && _sde.TryGetTypeName(typeId, out var name) ? name : $"type {typeId}";

    public string? GroupName(int typeId) =>
        _sde.IsAvailable && _sde.GetType(typeId) is { } type && _sde.GetGroup(type.GroupId) is { } group
            ? group.Name : null;
}
