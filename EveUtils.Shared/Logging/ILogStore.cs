namespace EveUtils.Shared.Logging;

/// <summary>
/// Stores captured log entries and notifies subscribers when new entries arrive.
/// The store is a thread-safe ring-buffer; oldest entries are evicted when the capacity is reached.
/// </summary>
public interface ILogStore
{
    void Add(LogEntry entry);

    IReadOnlyList<LogEntry> GetAll();

    void Clear();

    /// <summary>Raised on the calling thread whenever a new entry is added.</summary>
    event Action<LogEntry> EntryAdded;
}
