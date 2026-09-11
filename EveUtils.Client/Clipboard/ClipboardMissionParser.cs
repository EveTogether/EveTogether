using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.Clipboard;

/// <summary>An unrecognised reward line still gets an entry, with <see cref="ParameterKey"/> null, so it is never silently dropped.</summary>
public sealed record ClipboardMissionReward(RunParameterKey? ParameterKey, decimal? Amount, string? ItemName,
    long? ItemQuantity, string RawLine);

/// <summary><see cref="ObjectivesHeaderName"/> is the header line's own name, not the mission's — this capture never states one.</summary>
public sealed record ClipboardMissionCapture(string? ObjectivesHeaderName, string? AgentName, int? BonusWindowSeconds,
    IReadOnlyList<ClipboardMissionReward> Rewards, bool IsImportantMission);

/// <summary>The location row next to "Report to &lt;agent&gt;" is never read: the agent name alone is the resolving key (ET-172 sub 1), and the location text is free-form prose no parser should trust.</summary>
public static partial class ClipboardMissionParser
{
    private const string ObjectivesHeaderSuffix = " Objectives";
    private const string ReportToPrefix = "Report to ";

    // ET-251: the same preface EVE shows above the header for an important (storyline) mission — the only signal
    // this project has measured for the flag, so matched literally rather than on a looser "important" keyword.
    private const string ImportantMissionPreface =
        "This is an important mission, which will have significant impact on your faction standings.";

    // ET-251: an important mission's header sits one line below this preface, so the header is looked for among
    // the first couple of non-empty lines, the same bound ClipboardShapeRecogniser.IsMissionShape uses.
    private const int MaxHeaderSearchLines = 2;

    // ET-251: the one real capture with an item reward uses a plain "x" ("1 x Cybernetic Subprocessor -
    // Standard"), never measured against a "×" (multiplication sign) before this — that shape stays accepted too,
    // in case a client somewhere still emits it (EVE Journal's own regex, measured against their source during
    // ET-172's grooming, not a live client). The quantity may carry a thousands separator (ET-251).
    [GeneratedRegex(@"^(?<qty>[\d.,]+)\s*[x×]\s*(?<name>.+)$")]
    private static partial Regex ItemRewardPattern();

    [GeneratedRegex(@"within (?:(?<hours>\d+) hours?(?: and (?<minutes>\d+) minutes?)?|(?<minutes>\d+) minutes?)")]
    private static partial Regex BonusWindowPattern();

    public static ClipboardMissionCapture? Parse(string text)
    {
        string? objectivesHeaderName = null;
        string? agentName = null;
        int? bonusWindowSeconds = null;
        var isImportantMission = false;
        var rewards = new List<ClipboardMissionReward>();
        var block = RewardBlock.None;
        var headerSearchOpen = true;
        var nonEmptyLinesSeen = 0;

        foreach (var clipboardLine in text.Split('\n'))
        {
            string rawLine = clipboardLine.EndsWith('\r') ? clipboardLine[..^1] : clipboardLine;
            string line = rawLine;
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (headerSearchOpen)
            {
                nonEmptyLinesSeen++;
                if (trimmed.Length > ObjectivesHeaderSuffix.Length && trimmed.EndsWith(ObjectivesHeaderSuffix, StringComparison.Ordinal))
                {
                    objectivesHeaderName = trimmed[..^ObjectivesHeaderSuffix.Length];
                    headerSearchOpen = false;
                    continue;
                }

                if (trimmed == ImportantMissionPreface)
                {
                    isImportantMission = true;
                    if (nonEmptyLinesSeen >= MaxHeaderSearchLines)
                        headerSearchOpen = false;
                    continue;
                }

                if (nonEmptyLinesSeen >= MaxHeaderSearchLines)
                    headerSearchOpen = false;
            }

            if (trimmed == "Rewards")
            {
                block = RewardBlock.Rewards;
                continue;
            }

            if (trimmed == "Bonus Rewards")
            {
                block = RewardBlock.BonusRewards;
                continue;
            }

            if (agentName is null && line.StartsWith(ReportToPrefix, StringComparison.Ordinal))
            {
                agentName = line[ReportToPrefix.Length..].Trim();
                continue;
            }

            // A reward content row is indented ("\t1.000.000 ISK"); the explanatory sentence above it is not, and
            // the location row lives in the Objectives block where this branch never runs (block == None there).
            if (block != RewardBlock.None && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                rewards.Add(ParseReward(rawLine, LastTabField(line), block == RewardBlock.Rewards ? RunParameterKey.Isk : RunParameterKey.BonusIsk));
                continue;
            }

            if (block == RewardBlock.BonusRewards && bonusWindowSeconds is null)
            {
                var match = BonusWindowPattern().Match(line);
                if (match.Success)
                {
                    // Either half can be absent ("within 6 hours", "within 45 minutes"), never both — the
                    // alternation in BonusWindowPattern already refuses a match with neither.
                    long hours = 0, minutes = 0;
                    var hasHours = match.Groups["hours"].Success
                        && ClipboardInventoryParser.TryParseWholeNumber(match.Groups["hours"].Value, out hours);
                    var hasMinutes = match.Groups["minutes"].Success
                        && ClipboardInventoryParser.TryParseWholeNumber(match.Groups["minutes"].Value, out minutes);
                    if (hasHours || hasMinutes)
                        bonusWindowSeconds = (int)(hours * 3600 + minutes * 60);
                }
            }
        }

        return objectivesHeaderName is null && agentName is null && rewards.Count == 0 && bonusWindowSeconds is null && !isImportantMission
            ? null
            : new ClipboardMissionCapture(objectivesHeaderName, agentName, bonusWindowSeconds, rewards, isImportantMission);
    }

    private static ClipboardMissionReward ParseReward(string rawLine, string value, RunParameterKey iskKind)
    {
        var trimmed = value.Trim();

        if (trimmed.EndsWith(" ISK", StringComparison.Ordinal)
            && TryParseWholeRewardAmount(trimmed[..^" ISK".Length].TrimEnd(), out var amount))
            return new ClipboardMissionReward(iskKind, amount, null, null, rawLine);

        if (trimmed.EndsWith(" Loyalty Points", StringComparison.Ordinal)
            && TryParseWholeRewardAmount(trimmed[..^" Loyalty Points".Length].TrimEnd(), out var loyaltyPoints))
            return new ClipboardMissionReward(RunParameterKey.LoyaltyPoints, loyaltyPoints, null, null, rawLine);

        // Same reward-line shape as Loyalty Points above — inferred from it, not measured against a capture of its
        // own (ET-237): no real "copy all" with an Evermarks line has reached this project yet.
        if (trimmed.EndsWith(" Evermarks", StringComparison.Ordinal)
            && TryParseWholeRewardAmount(trimmed[..^" Evermarks".Length].TrimEnd(), out var evermarks))
            return new ClipboardMissionReward(RunParameterKey.Evermarks, evermarks, null, null, rawLine);

        var itemMatch = ItemRewardPattern().Match(trimmed);
        if (itemMatch.Success && ClipboardInventoryParser.TryParseWholeNumber(itemMatch.Groups["qty"].Value, out var quantity))
            return new ClipboardMissionReward(RunParameterKey.Item, null, itemMatch.Groups["name"].Value.Trim(), quantity, rawLine);

        return new ClipboardMissionReward(null, null, null, null, rawLine);
    }

    // ISK and Loyalty Points mission rewards are always whole numbers in EVE, never decimals, so a single
    // "360,000"-style separator can only be a thousands mark here — unlike ClipboardInventoryParser.TryParseLocalNumber,
    // which must stay ambiguous about that same shape because an inventory price genuinely can carry a decimal
    // (ET-238). Reuses the same trap-free whole-number reader already shared for item quantities and the bonus-window
    // hour/minute counts, after folding the space-family group separators that reader does not itself expect.
    private static bool TryParseWholeRewardAmount(string value, out decimal amount)
    {
        var normalized = value.Replace(' ', '.').Replace(' ', '.').Replace(' ', '.');
        if (ClipboardInventoryParser.TryParseWholeNumber(normalized, out var whole))
        {
            amount = whole;
            return true;
        }

        amount = default;
        return false;
    }

    private static string LastTabField(string line)
    {
        return line.Split('\t', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[^1];
    }

    private enum RewardBlock { None, Rewards, BonusRewards }
}
