using System;
using System.Collections.Generic;

namespace EveUtils.Client.Dialogs;

/// <summary>One started fleet this character is walking out of, and since when it has been running.</summary>
public sealed record SwitchFleetLeaving(string FleetName, DateTimeOffset? ActivatedAt);

/// <summary>
/// What the switch dialog is told (ET-168, scherm 7). Switching is <b>the member's own act</b>: a commander asks,
/// and may move their own alt because it is their character — never because they are the commander. So this dialog
/// only ever describes one of my own pilots moving itself.
///
/// <para>It exists to make two acts feel like one. In the code a switch is leave-then-couple, and accepting while
/// still active elsewhere is refused outright (<c>ActiveFleetMembershipGuard</c>: <i>leave or conclude it before
/// joining another</i>). Rather than hide that or report it as an error afterwards, the dialog spells the steps out
/// and puts one button under them.</para>
/// </summary>
/// <param name="CharacterName">The pilot being moved — always one of mine.</param>
/// <param name="TargetFleetName">The fleet they will count for afterwards.</param>
/// <param name="TargetActivatedAt">When that fleet started, or null when it was never stamped.</param>
/// <param name="Leaving">The started fleets they are walking out of. Normally one; more than one only when this
/// client can see the pilot rostered in several started fleets at once.</param>
/// <param name="RunsInProgress">This client's runs still going for this pilot, as <c>"Kaska Vex — Fortress Sansha,
/// 00:11:42"</c> lines. Shown because a switch must not read as something that throws a measurement away.</param>
public sealed record SwitchFleetPrompt(
    string CharacterName,
    string TargetFleetName,
    DateTimeOffset? TargetActivatedAt,
    IReadOnlyList<SwitchFleetLeaving> Leaving,
    IReadOnlyList<string> RunsInProgress);
