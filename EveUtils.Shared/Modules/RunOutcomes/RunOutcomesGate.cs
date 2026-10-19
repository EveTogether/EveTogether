using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Permissions.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes;

/// <summary>The control panel's <see cref="RunOutcomesModule.EnabledToggle"/>, read where an outcome is taken or handed
/// out. Taking one's own outcomes back is never gated.</summary>
internal static class RunOutcomesGate
{
    public static ResultMessage? Refusal(IPermissionToggleStore toggles) =>
        toggles.IsEnabled(RunOutcomesModule.EnabledToggle)
            ? null
            : new ResultMessage(MessageSeverity.Error, MessageCodes.PermissionDenied,
                "Sharing run outcomes is switched off on this server.", "RunOutcomes");

    /// <summary>The window a read covers: from <paramref name="since"/>, but never further back than the statistic's
    /// fallback window.</summary>
    public static DateOnly Since(DateOnly since)
    {
        DateOnly earliest = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-RunOutcomesModule.WidestWindowDays);
        return since < earliest ? earliest : since;
    }
}
