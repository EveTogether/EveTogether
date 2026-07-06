using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Stores the pilot's own client's can-fly verdict for their assigned fit. Trained skills never
/// leave the pilot's client — only this verdict travels, so other clients can show the can-fly / warning badge for pilots
/// whose skills they do not know locally. Self-only (like join/leave): the acting character must BE the member.
/// The result's value reports whether the stored verdict changed, so the caller broadcasts only real changes.
/// </summary>
public sealed record ReportMemberFitVerdictCommand(
    long MemberId, FitSkillVerdict Verdict, int ActingCharacterId) : ICommand<Result<bool>>;
