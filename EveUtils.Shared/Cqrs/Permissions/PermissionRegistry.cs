namespace EveUtils.Shared.Cqrs.Permissions;

/// <summary>Code-derived permission registry; codes must be unique across modules (startup validation).</summary>
public sealed class PermissionRegistry : IPermissionRegistry
{
    private readonly Dictionary<string, PermissionDescriptor> _byCode;

    public PermissionRegistry(IEnumerable<IPermissionCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        _byCode = new Dictionary<string, PermissionDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in catalogs.SelectMany(c => c.Descriptors))
        {
            if (!_byCode.TryAdd(descriptor.Code, descriptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate permission code '{descriptor.Code}' " +
                    $"(modules '{_byCode[descriptor.Code].Module}' and '{descriptor.Module}').");
            }
        }
    }

    public bool Contains(string code) => _byCode.ContainsKey(code);

    public IReadOnlyCollection<PermissionDescriptor> All() => _byCode.Values;
}
