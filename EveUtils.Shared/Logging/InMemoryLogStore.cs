using System.Collections.Concurrent;

namespace EveUtils.Shared.Logging;

/// <summary>
/// Thread-safe ring-buffer log store (capacity = 500 by default). Optionally appends to a rolling
/// JSON-Lines file in the data directory. The file cap keeps disk usage bounded.
/// </summary>
public sealed class InMemoryLogStore : ILogStore
{
    private const int DefaultCapacity = 500;
    private const int MaxFileEntries = 2000;

    private readonly int _capacity;
    private readonly string? _filePath;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly Lock _fileLock = new();

    public event Action<LogEntry> EntryAdded = _ => { };

    public InMemoryLogStore(int capacity = DefaultCapacity, string? dataDirectory = null)
    {
        _capacity = capacity;
        if (dataDirectory is not null)
            _filePath = Path.Combine(dataDirectory, "app-errors.jsonl");
    }

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);

        while (_entries.Count > _capacity)
            _entries.TryDequeue(out _);

        AppendToFile(entry);

        try { EntryAdded(entry); }
        catch { /* subscribers must not crash the logger */ }
    }

    public IReadOnlyList<LogEntry> GetAll() => [.. _entries];

    public void Clear() => _entries.Clear();

    private void AppendToFile(LogEntry entry)
    {
        if (_filePath is null) return;
        try
        {
            lock (_fileLock)
            {
                // Roll: keep only MaxFileEntries lines.
                if (File.Exists(_filePath))
                {
                    var lines = File.ReadAllLines(_filePath);
                    if (lines.Length >= MaxFileEntries)
                        File.WriteAllLines(_filePath, lines[^(MaxFileEntries / 2)..]);
                }

                var json = System.Text.Json.JsonSerializer.Serialize(entry);
                File.AppendAllText(_filePath, json + Environment.NewLine);
            }
        }
        catch { /* best-effort */ }
    }
}
