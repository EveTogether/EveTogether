using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// One intended copy, complete enough to show the user before anything is written: which profile, from whom, to
/// whom, and how many files that is. Built and inspected first, then handed to <see cref="SettingsSyncService"/> —
/// the same plan the scheduled sync builds (ET-60), which is why nothing here knows about the UI.
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

/// <summary>What a sync did: which targets took the new settings, which did not and why, and the backup taken
/// beforehand — the whole snapshot, so the caller can say what is in it rather than only where it is.</summary>
public sealed record SettingsSyncOutcome(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Failed,
    SettingsBackup Backup,
    bool Aborted = false);

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
/// Deliberately free of UI and of any "ask the user" step: the backup is not a choice, and the same call is what the
/// automatic sync (ET-60) makes once it has decided the clients are closed.
/// </summary>
public sealed class SettingsSyncService(SettingsBackupService backups) : ISingletonService
{
    /// <summary>
    /// Validates the plan, snapshots the profile, then overwrites each target's contents. Returns a failure without
    /// touching a single file when the plan does not hold up or the backup cannot be written.
    /// </summary>
    public Result<SettingsSyncOutcome> Apply(
        SettingsSyncPlan plan, IReadOnlyDictionary<long, string> names, Func<bool>? abortWhen = null) =>
        ApplyAll([plan], names, BackupReason.BeforeSync, null, abortWhen);

    /// <summary>
    /// Several plans in one go — a character rule and an account rule together — against one profile and behind
    /// <em>one</em> backup. The automatic sync uses this: two separate calls would leave two snapshots per run, the
    /// second of them already half-synced and therefore no longer a picture of where the user was.
    /// </summary>
    /// <param name="abortWhen">
    /// Checked before the backup and again before every single file. The automatic sync passes "is an EVE client
    /// running?" here: a client that starts mid-run would rewrite whatever we put down when it closes, so stopping
    /// part-way and saying so beats finishing into a client that will undo it.
    /// </param>
    public Result<SettingsSyncOutcome> ApplyAll(
        IReadOnlyList<SettingsSyncPlan> plans,
        IReadOnlyDictionary<long, string> names,
        BackupReason reason = BackupReason.BeforeSync,
        string? note = null,
        Func<bool>? abortWhen = null)
    {
        if (plans.Count == 0)
            return _Refuse("There is nothing to copy.");

        foreach (var plan in plans)
        {
            if (plan.Targets.Count == 0)
                return _Refuse("Pick at least one target to copy to.");

            if (plan.Targets.Any(target => target.Kind != plan.Source.Kind))
                return _Refuse("Character settings and account settings cannot be copied onto each other.");

            if (plan.Targets.Any(target => target.Id == plan.Source.Id))
                return _Refuse("The source cannot also be a target.");
        }

        if (plans.Select(plan => plan.Profile.DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return _Refuse("Every copy in one run has to be inside the same profile.");

        var payloads = new List<byte[]>(plans.Count);
        foreach (var plan in plans)
        {
            try
            {
                payloads.Add(File.ReadAllBytes(plan.Source.FullPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<SettingsSyncOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
                    MessageCodes.FileIoFailed, $"Could not read {plan.SourceName}'s settings: {ex.Message}"));
            }
        }

        if (abortWhen?.Invoke() == true)
            return _Refuse("An EVE client is running, so nothing was copied.");

        // From here on files change: the automatic sync and a button press must not interleave their backup and
        // their writes.
        using var gate = EveSettingsWriteGate.Acquire();

        var first = plans[0];
        var backup = backups.Create(first.Profile, first.InstallRoot, names, reason, note ?? _Note(plans));
        if (!backup.IsSuccess || backup.Value is null)
            return Result<SettingsSyncOutcome>.Failure(backup.Messages.ToArray());

        var copied = new List<string>();
        var failed = new List<string>();
        var aborted = false;
        foreach (var (plan, payload) in plans.Zip(payloads))
        {
            for (var index = 0; index < plan.Targets.Count; index++)
            {
                var target = plan.Targets[index];
                var name = index < plan.TargetNames.Count ? plan.TargetNames[index] : target.FileName;

                if (aborted || abortWhen?.Invoke() == true)
                {
                    aborted = true;
                    failed.Add($"{name}: not copied, an EVE client started");
                    continue;
                }

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
        }

        return Result<SettingsSyncOutcome>.Success(new SettingsSyncOutcome(copied, failed, backup.Value, aborted));
    }

    /// <summary>
    /// The targets whose contents differ from the source — the ones a copy would actually change. The automatic sync
    /// asks this before doing anything: running on a timer and copying regardless would leave a pile of backups that
    /// all say the same thing, and each one would push a real one further out of reach. A file that cannot be read
    /// counts as different, so an unreadable target is attempted (and reported) rather than silently skipped forever.
    /// </summary>
    public static IReadOnlyList<EveSettingsFile> OutOfSync(EveSettingsFile source, IEnumerable<EveSettingsFile> targets)
    {
        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(source.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];   // unreadable source: nothing to copy from, and the caller reports the miss
        }

        return targets.Where(target => !_SameContent(target, payload)).ToList();
    }

    private static bool _SameContent(EveSettingsFile target, byte[] payload)
    {
        try
        {
            if (target.SizeBytes != payload.LongLength)
                return false;
            return File.ReadAllBytes(target.FullPath).AsSpan().SequenceEqual(payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Result<SettingsSyncOutcome> _Refuse(string message) =>
        Result<SettingsSyncOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
            MessageCodes.ValidationFailed, message));

    private static string _Note(IReadOnlyList<SettingsSyncPlan> plans) => string.Join("; ", plans.Select(plan =>
        $"before copying {plan.SourceName} to {plan.FileCount} {_KindLabel(plan.Kind, plan.FileCount)}"));

    private static string _KindLabel(SettingsFileKind kind, int count) => kind switch
    {
        SettingsFileKind.Character => count == 1 ? "character" : "characters",
        _ => count == 1 ? "account" : "accounts"
    };
}
