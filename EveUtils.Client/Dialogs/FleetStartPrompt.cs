using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// One pilot as the start dialog draws them (ET-168, scherm 2): who they are to me, and whether they are already
/// counting for another started fleet.
/// </summary>
/// <param name="ElsewhereFleetName">The started fleet this pilot counts for instead, or null when they are free.
/// Present means "elsewhere active" — never "offline", which in this app means not logged in and nothing else.</param>
public sealed record FleetStartMember(
    int CharacterId,
    string Name,
    bool IsMine,
    bool IsCommander,
    bool IsExternal,
    string? ElsewhereFleetName)
{
    public bool IsElsewhereActive => !string.IsNullOrEmpty(ElsewhereFleetName);

    /// <summary>The tail of the roster line: what starting will do for this pilot, and — when it will do nothing —
    /// why. Whose pilot it is comes first, because that is what decides whether the answer is "switch them" or
    /// "ask them".</summary>
    public string StateText => IsExternal
        ? "no client · never shares"
        : IsElsewhereActive
            ? (IsMine ? $"your pilot · in {ElsewhereFleetName}" : $"someone else's pilot · in {ElsewhereFleetName}")
            : "free · will be linked";
}

/// <summary>What the commander decided in the start dialog (ET-168, scherm 2).</summary>
public enum FleetStartChoice
{
    /// <summary>Backed out. Nothing started, nothing asked.</summary>
    Cancel,

    /// <summary>Start, and leave whoever is elsewhere where they are. <b>The default</b>, and not the same act as
    /// taking them off the roster: they stay members, merely not linked, so switching an hour later still works.
    /// An earlier design let "start without them" clear them off the roster, which shut exactly that door.</summary>
    LeaveThem,

    /// <summary>Start, and send every member who is active elsewhere a request to come over — one member or fifty,
    /// the same single act. A request and nothing more: no one is moved and no roster is touched.</summary>
    AskThemAll,
}

/// <summary>
/// What the start dialog is told about the fleet it is asked to start (ET-168, scherm 2). A carrier: the caller has
/// the roster loaded already and the dialog shows it back, so the commander decides against the fleet's actual state
/// rather than from its name.
///
/// <para>The collision is always one summary line and one button, whether one member is elsewhere or fifty. There is
/// deliberately no per-member choice here and no count above which the shape changes: starting is one act, and a
/// member-by-member form is what turns two collisions into paperwork. The per-member choice lives outside starting,
/// on the member row in the overview and in fleet management.</para>
/// </summary>
/// <param name="FleetName">The fleet, named in the header chip.</param>
/// <param name="Members">Its whole roster, externals included.</param>
/// <param name="CanAskThemAll">Whether there is anyone with an inbox to ask. False for a client-only fleet, whose
/// pilots are the owner's own characters: those you move yourself from the member row, because they are your
/// characters — not because you are the commander.</param>
/// <remarks>Under-strength is deliberately <i>not</i> here. It is asked before this dialog opens, because a
/// question that has to be answered cannot scroll out of view and a note in a long dialog can.</remarks>
public sealed record FleetStartPrompt(
    string FleetName,
    IReadOnlyList<FleetStartMember> Members,
    bool CanAskThemAll)
{
    /// <summary>Everyone on the roster who counts for another started fleet — the collision, in roster order.</summary>
    public IReadOnlyList<FleetStartMember> ActiveElsewhere { get; } =
        Members.Where(m => m.IsElsewhereActive).ToList();

    /// <summary>Pilots with a client of their own: the ones a start can link. An external is on the roster on trust
    /// and shares nothing either way, so counting them as "available" would overstate what starting achieves.</summary>
    public int AvailableCount { get; } = Members.Count(m => !m.IsExternal);

    public int ExternalCount { get; } = Members.Count(m => m.IsExternal);
    public int MineCount { get; } = Members.Count(m => m.IsMine);
    public int RosterCount => Members.Count;

    public int ElsewhereCount => ActiveElsewhere.Count;
    public bool HasCollision => ElsewhereCount > 0;

    /// <summary>My own characters among the colliding ones. Named apart because they are the one case the dialog
    /// does <i>not</i> ask about: your own alt you move yourself, from the member row.</summary>
    public IReadOnlyList<FleetStartMember> MyAltsElsewhere { get; } =
        Members.Where(m => m.IsElsewhereActive && m.IsMine).ToList();

    /// <summary>Whoever will be linked the moment this starts: on the roster, with a client, free.</summary>
    public int WillLinkCount => Members.Count(m => !m.IsExternal && !m.IsElsewhereActive);
}
