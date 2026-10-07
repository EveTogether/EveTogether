using System.Diagnostics;
using System.Globalization;

namespace EveUtils.Client.Composition;

/// <summary>
/// One client per data directory (ET-465). Two clients on one store each record every copied loot listing and keep
/// their own in-process view of a run, so a SAVE in one leaves the other's window and run list behind on a run that
/// is already saved. The lock is an exclusive OS lock on a file inside the directory itself: an instance with its own
/// directory (<see cref="ClientDataLocation.InstanceVariable"/>) has its own lock, and the OS lets go of it when the
/// holding process ends, however it ends — a file left behind by a crash locks nothing.
/// </summary>
public sealed class ClientInstanceLock : IDisposable
{
    private const string LockFileName = "instance.lock";
    private const string OwnerFileName = "instance.pid";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly FileStream _lock;

    private ClientInstanceLock(FileStream lockStream) => _lock = lockStream;

    /// <summary>The lock on <paramref name="dataDirectory"/>, waiting up to <paramref name="patience"/> for a holder that
    /// is on its way out — the version Velopack just replaced, or a client closed a moment ago — before giving up with
    /// null.</summary>
    public static ClientInstanceLock? Acquire(string dataDirectory, TimeSpan patience)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (TryAcquire(dataDirectory) is { } acquired)
                return acquired;
            if (waited.Elapsed >= patience)
                return null;

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>The lock on <paramref name="dataDirectory"/>, or null while another client holds it.</summary>
    public static ClientInstanceLock? TryAcquire(string dataDirectory)
    {
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(Path.Combine(dataDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        // Beside the lock rather than in it: nobody else can open the locked file to read who holds it.
        File.WriteAllText(Path.Combine(dataDirectory, OwnerFileName), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return new ClientInstanceLock(lockStream);
    }

    /// <summary>The process that last took the lock on <paramref name="dataDirectory"/>, if it said so.</summary>
    public static int? OwnerProcessId(string dataDirectory)
    {
        try
        {
            string text = File.ReadAllText(Path.Combine(dataDirectory, OwnerFileName));
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) ? processId : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _lock.Dispose();
}
