using System;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.Runs;

/// <summary>What an activity contributes to a total: when it started, how long it was flown and what this machine's
/// own characters made of it. A row on screen is one, and so is an activity the strip (ET-292) counts without ever
/// building a row for it — which is how a day header, a strip cell and the range line add up the very same figures.</summary>
public interface IRunsActivityFigures
{
    DateTime StartedAtLocal { get; }

    TimeSpan Duration { get; }

    /// <summary>The own share's total, or null where nothing of it was valued.</summary>
    decimal? NetIsk { get; }

    IskBreakdown Isk { get; }
}
