using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// One intended copy, complete enough to show the user before anything is written: which profile, from whom, to
/// whom, and how many files that is. Built and inspected first, then handed to <see cref="SettingsSyncService"/> —
/// the same plan a scheduled sync would build (ET-60), which is why nothing here knows about the UI.
/// </summary>
public sealed record SettingsSyncPlan(
    EveSettingsProfile Profile,
    string InstallRoot,
    EveSettingsFile Source,
    string SourceName,
    IReadOnlyList<EveSettingsFile> Targets,
    IReadOnlyList<string> TargetNames)
{
    public SettingsFileKind Kind => Source.Kind;

    public int FileCount => Targets.Count;
}

/// <summary>What a sync did: which targets took the new settings, which did not and why, and where the backup is.</summary>
public sealed record SettingsSyncOutcome(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Failed,
    string BackupDirectory);

/// <summary>
/// Copies one character's or one account's EVE settings over other files of the same kind, after backing the whole
/// profile up. Two rules hold the whole feature up:
///
/// <list type="number">
/// <item>Character and account files never mix. A plan whose targets are not all of the source's kind is refused
/// here, not just discouraged in the UI.</item>
/// <item>A target keeps its own file name and id — only the bytes inside change. Copying the source file <em>as</em>
/// the target's name would make EVE load one character's settings under another's identity.</item>
/// </list>
///
/// Deliberately free of UI and of any "ask the user" step: the backup is not a choice, and the same call is what a
/// later automatic sync (ET-60) will make once it has decided the clients are closed.
/// </summary>
public sealed class SettingsSyncService(SettingsBackupService backups) : ISingletonService
{
    /// <summary>
    /// Validates the plan, snapshots the profile, then overwrites each target's contents. Returns a failure without
    /// touching a single file when the plan does not hold up or the backup cannot be written.
    /// </summary>
    public Result<SettingsSyncOutcome> Apply(SettingsSyncPlan plan, IReadOnlyDictionary<long, string> names)
    {
        if (plan.Targets.Count == 0)
            return Result<SettingsSyncOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.ValidationFailed, "Pick at least one target to copy to."));

        if (plan.Targets.Any(target => target.Kind != plan.Source.Kind))
            return Result<SettingsSyncOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.ValidationFailed, "Character settings and account settings cannot be copied onto each other."));

        if (plan.Targets.Any(target => target.Id == plan.Source.Id))
            return Result<SettingsSyncOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.ValidationFailed, "The source cannot also be a target."));

        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(plan.Source.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<SettingsSyncOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.FileIoFailed, $"Could not read {plan.SourceName}'s settings: {ex.Message}"));
        }

        var backup = backups.Create(plan.Profile, plan.InstallRoot, names, BackupReason.BeforeSync,
            $"before copying {plan.SourceName} to {plan.FileCount} {_KindLabel(plan.Kind, plan.FileCount)}");
        if (!backup.IsSuccess || backup.Value is null)
            return Result<SettingsSyncOutcome>.Failure(backup.Messages.ToArray());

        var copied = new List<string>();
        var failed = new List<string>();
        for (var index = 0; index < plan.Targets.Count; index++)
        {
            var target = plan.Targets[index];
            var name = index < plan.TargetNames.Count ? plan.TargetNames[index] : target.FileName;
            try
            {
                File.WriteAllBytes(target.FullPath, payload);
                copied.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{name}: {ex.Message}");
            }
        }

        return Result<SettingsSyncOutcome>.Success(
            new SettingsSyncOutcome(copied, failed, backup.Value.DirectoryPath));
    }

    private static string _KindLabel(SettingsFileKind kind, int count) => kind switch
    {
        SettingsFileKind.Character => count == 1 ? "character" : "characters",
        _ => count == 1 ? "account" : "accounts"
    };
}
