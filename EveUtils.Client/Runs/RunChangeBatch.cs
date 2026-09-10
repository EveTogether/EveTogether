using EveUtils.Shared.Modules.Runs.Events;

namespace EveUtils.Client.Runs;

/// <summary>
/// Every <see cref="RunsChangedEvent"/> that arrived within one <see cref="RunChangeFeed"/> window, folded into what a
/// screen needs to decide whether it has to read again: which runs, which groups, or "any run at all" when one of the
/// writes could not name the runs it touched.
/// </summary>
public sealed class RunChangeBatch
{
    private readonly HashSet<Guid> _runIds = [];
    private readonly HashSet<string> _groupCodes = new(StringComparer.Ordinal);

    /// <summary>True once a change arrived that named neither a run nor a group — a rebuild of every summary, the
    /// startup clean-up. Such a batch concerns every screen.</summary>
    public bool IsUnscoped { get; private set; }

    public IReadOnlyCollection<Guid> RunIds => _runIds;

    public IReadOnlyCollection<string> GroupCodes => _groupCodes;

    /// <summary>Whether a screen showing these runs, or this group, has anything to read again. A run that just
    /// joined the group is only ever recognisable by the group code, which is why both are asked.</summary>
    public bool Concerns(IEnumerable<Guid> runIds, string? groupCode) =>
        IsUnscoped || groupCode is not null && _groupCodes.Contains(groupCode) || runIds.Any(_runIds.Contains);

    internal void Add(RunsChangedEventData change)
    {
        if (change.RunId is { } runId)
            _runIds.Add(runId);
        if (change.GroupCode is { } groupCode)
            _groupCodes.Add(groupCode);
        if (change is { RunId: null, GroupCode: null })
            IsUnscoped = true;
    }

    internal void Add(RunChangeBatch other)
    {
        _runIds.UnionWith(other._runIds);
        _groupCodes.UnionWith(other._groupCodes);
        IsUnscoped |= other.IsUnscoped;
    }
}
