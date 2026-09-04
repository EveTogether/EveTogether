using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// What the availability dialog is told (ET-169, scherm 7): whose availability this is, for which Forming
/// fleet, and what it is set to right now. <see cref="Current"/> is <see cref="FleetMemberAvailability.NotSet"/>
/// the first time the member opens this — silence, not a "no" — so the dialog defaults to "yes, count me in"
/// exactly as an unopened roster row already reads.
/// </summary>
public sealed record FleetAvailabilityPrompt(
    string CharacterName, string FleetName, FleetMemberAvailability Current, string? CurrentNote);

/// <summary>What the member chose: available (or unset, read back the same way) or signed off, with the
/// optional note that goes with a sign-off. Null from the dialog call means cancelled — nothing is sent.</summary>
public sealed record FleetAvailabilitySubmission(FleetMemberAvailability Availability, string? Note);
