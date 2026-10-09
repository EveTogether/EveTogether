using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// The start dialog's roster for as long as the dialog is open (ET-492). The screen that opens it goes on hearing who
/// comes online and who connects, and calls <see cref="Refresh"/> when it does; the dialog redraws on
/// <see cref="Changed"/> instead of showing the roster as it stood when START was pressed.
/// </summary>
/// <param name="prompt">The roster as the dialog opens on it.</param>
/// <param name="readMembers">The members as the opening screen reads them now; null once it no longer shows the
/// fleet, which leaves the dialog as it was.</param>
public sealed class FleetStartRoster(FleetStartPrompt prompt, Func<IReadOnlyList<FleetStartMember>?> readMembers)
{
    public FleetStartPrompt Current { get; private set; } = prompt;

    /// <summary>Raised on the UI thread with the new roster when a member reads differently.</summary>
    public event Action<FleetStartPrompt>? Changed;

    internal bool HasListeners => Changed is not null;

    public void Refresh()
    {
        if (readMembers() is not { } members || members.SequenceEqual(Current.Members))
            return;

        // A new prompt rather than a `with`: its counts are computed once, at construction.
        Current = new FleetStartPrompt(Current.FleetName, members, Current.CanAskThemAll);
        Changed?.Invoke(Current);
    }
}
