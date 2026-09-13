namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// A set of <see cref="CadenceRate"/> streams keyed by what tells them apart (a weapon, a source). Each stream keeps
/// its own cadence — a missile launcher and a drone flight must not share one — and their rates add up. A stream that
/// has been silent for a long while is forgotten, so a session against hundreds of differently named rats does not
/// grow without bound. Not thread-safe: the owning tracker serialises access.
/// </summary>
internal sealed class CadenceStreams
{
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, CadenceRate> _streams = new(StringComparer.Ordinal);

    public void Add(string key, DateTime at, int amount)
    {
        if (!_streams.TryGetValue(key, out var stream))
            _streams[key] = stream = new CadenceRate();
        stream.Add(at, amount);
    }

    public double Sample(DateTime now)
    {
        double total = 0;
        List<string>? forgotten = null;
        foreach (var (key, stream) in _streams)
        {
            if (now - stream.LastAt > ForgetAfter)
                (forgotten ??= []).Add(key);
            else
                total += stream.Sample(now);
        }

        if (forgotten is not null)
            foreach (var key in forgotten)
                _streams.Remove(key);

        return total;
    }
}
