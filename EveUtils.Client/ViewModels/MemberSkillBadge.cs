namespace EveUtils.Client.ViewModels;

/// <summary>The skill-gap verdict for a member's assigned fit, shown as a can-fly / warning badge in the roster
/// row. <see cref="CanFly"/> true = the pilot trains every skill the fit needs; false = at least one skill is short.
/// A null badge (no instance) means "no verdict" — the member has no assigned fit, or that character's skills are not
/// locally known, so no badge is shown rather than a red mark.</summary>
public sealed record MemberSkillBadge(bool CanFly, string Tooltip);
