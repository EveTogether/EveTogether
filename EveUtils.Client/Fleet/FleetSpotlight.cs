using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Fleet;

/// <summary>
/// One name the FC can act on right now — "who is taking the most damage", "who is being neuted the most" — held
/// steady while the figures underneath it move.
///
/// The naive answer, <c>OrderByDescending(value).First()</c>, is unreadable in flight: two members within a few
/// percent of each other trade places on every sample, so the one line that has to be read at a glance is the one
/// line that never stands still. Three rules make it hold:
///
/// <list type="bullet">
/// <item><b>A floor.</b> Below it there is no decision to make — a stray drone hit, one neut cycle at the edge of
/// range — so nobody is named at all. This is also what makes the quiet state quiet.</item>
/// <item><b>A margin.</b> A challenger takes the slot only when they are clearly ahead of the pilot standing in it,
/// not merely ahead. Near-equal members therefore do not swap; a real outlier still does.</item>
/// <item><b>A dwell.</b> Being clearly ahead has to last. One volley landing early is not a change of subject.</item>
/// </list>
///
/// The exception, and it is the point of the window: when nothing is being shown there is nothing to be steady
/// about, so the first pilot over the floor takes the slot <b>immediately</b>. Steadiness is what you want while
/// reading a name, never what you want before there is one.
/// </summary>
public sealed class FleetSpotlight
{
    /// <summary>One member's current figure for whatever this spotlight ranks.</summary>
    /// <param name="Name">The pilot's name — the whole answer. A rank without a name helps nobody.</param>
    /// <param name="Value">Their current rate, in the spotlight's own unit.</param>
    public readonly record struct Candidate(string Name, long Value);

    private readonly long _floor;
    private readonly double _switchMargin;
    private readonly TimeSpan _switchAfter;
    private readonly TimeSpan _quietAfter;

    private string? _challenger;
    private DateTime _challengerSince;
    private DateTime? _quietSince;

    /// <param name="floor">The rate below which nobody is named.</param>
    /// <param name="switchMargin">How far ahead a challenger must be, as a multiple of the current subject's figure.</param>
    /// <param name="switchAfter">How long they must stay that far ahead before the slot changes hands.</param>
    /// <param name="quietAfter">How long everyone stays under the floor before the slot empties. Longer than
    /// <paramref name="switchAfter"/> on purpose: a lull between volleys is not the end of a fight, and a window that
    /// blanks and refills between salvoes is worse to read than one that holds.</param>
    public FleetSpotlight(long floor, double switchMargin, TimeSpan switchAfter, TimeSpan quietAfter)
    {
        _floor = floor;
        _switchMargin = switchMargin;
        _switchAfter = switchAfter;
        _quietAfter = quietAfter;
    }

    /// <summary>The pilot currently named, or null when nothing is worth naming.</summary>
    public string? Name { get; private set; }

    /// <summary>The named pilot's current figure. Zero when there is no subject.</summary>
    public long Value { get; private set; }

    /// <summary>Whether there is anything to act on at all — the difference between the alert and the quiet state.</summary>
    public bool HasSubject => Name is not null;

    /// <summary>
    /// Take in the whole fleet's current figures and decide who, if anyone, the slot names. Call it as often as you
    /// like: the dwell is measured against <paramref name="now"/>, not against a number of calls, so the behaviour
    /// does not change with the refresh rate.
    /// </summary>
    public void Update(IReadOnlyList<Candidate> candidates, DateTime now)
    {
        // A subject who is no longer among the candidates has left the fleet or stopped reporting. There is no figure
        // of theirs left to show, so holding their name would be holding a claim nothing supports — drop it at once
        // and let the rules below fill the slot from scratch.
        if (Name is string held && !candidates.Any(c => Matches(c, held)))
            Clear();

        // Highest first, and on a tie the name — so two members sitting on the same figure cannot swap places
        // because the member collection happened to reorder.
        Candidate? top = candidates
            .Where(c => c.Value >= _floor && !string.IsNullOrWhiteSpace(c.Name))
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => (Candidate?)c)
            .FirstOrDefault();

        if (Name is not string subject)
        {
            // Nothing is shown, so nothing has to be protected from changing: the first pilot over the floor is named
            // this instant. Waiting here would be waiting to report the thing the window exists to report.
            if (top is { } first)
                Adopt(first);
            ResetPending();
            return;
        }

        // The subject's own current figure, whether or not they still lead — what is shown is always theirs.
        Value = candidates.First(c => Matches(c, subject)).Value;

        if (top is not { } leader)
        {
            // Everyone is under the floor. Hold the last name for a moment before going quiet.
            _quietSince ??= now;
            if (now - _quietSince >= _quietAfter)
                Clear();
            ResetPending();
            return;
        }

        _quietSince = null;

        if (Matches(leader, subject))
        {
            Value = leader.Value;
            ResetPending();
            return;
        }

        // Someone else leads. Clearly ahead, and still ahead a moment later, or the slot does not move.
        if (leader.Value >= Value * _switchMargin)
        {
            if (!string.Equals(_challenger, leader.Name, StringComparison.Ordinal))
            {
                _challenger = leader.Name;
                _challengerSince = now;
            }

            if (now - _challengerSince >= _switchAfter)
            {
                Adopt(leader);
                ResetPending();
            }

            return;
        }

        // Ahead, but not clearly: the two are close enough that swapping would be noise.
        ResetPending();
    }

    private static bool Matches(Candidate candidate, string name) =>
        string.Equals(candidate.Name, name, StringComparison.Ordinal);

    private void Adopt(Candidate candidate)
    {
        Name = candidate.Name;
        Value = candidate.Value;
        _quietSince = null;
    }

    private void Clear()
    {
        Name = null;
        Value = 0;
        _quietSince = null;
    }

    private void ResetPending()
    {
        _challenger = null;
        _challengerSince = default;
    }
}
