using System.Collections.Generic;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a composition fit-entry (gRPC <c>FleetCompositionEntryDto</c>). <see cref="SkillMinimums"/>
/// is the doctrine skill minimum on top of what the fit requires.</summary>
public sealed record FleetCompositionEntryInfo(
    long Id,
    long RoleId,
    int? EntryMinCount,
    int SortOrder,
    FitReferenceInfo Fit,
    IReadOnlyList<SkillMinimum> SkillMinimums);
