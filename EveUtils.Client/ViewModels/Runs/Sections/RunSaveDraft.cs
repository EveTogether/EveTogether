using System;
using System.Collections.Generic;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// What SAVE stores for one run of the group, filled in by the sections before the window sends it (ET-236). One per
/// run: the window's own run and each of the pilot's other toons in the group (ET-210) get their own.
/// </summary>
public sealed class RunSaveDraft(Guid runId, int? characterId, bool isActingRun)
{
    public Guid RunId { get; } = runId;

    /// <summary>Null for the window's own run while it has no character settled.</summary>
    public int? CharacterId { get; } = characterId;

    /// <summary>The run the window is showing, as opposed to a sibling saved alongside it.</summary>
    public bool IsActingRun { get; } = isActingRun;

    public List<RunEnemyObservationInput> Enemies { get; } = [];

    public List<RunParameterInput> Parameters { get; } = [];

    public RunLootStrategy? LootStrategy { get; set; }
}
