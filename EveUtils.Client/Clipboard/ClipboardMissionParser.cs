using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.Clipboard;

/// <summary>An unrecognised reward line still gets an entry, with <see cref="ParameterKey"/> null, so it is never silently dropped.</summary>
public sealed record ClipboardMissionReward(RunParameterKey? ParameterKey, decimal? Amount, string? ItemName, long? ItemQuantity);

/// <summary><see cref="ObjectivesHeaderName"/> is the header line's own name, not the mission's — this capture never states one.</summary>
public sealed record ClipboardMissionCapture(string? ObjectivesHeaderName, string? AgentName, int? BonusWindowSeconds,
    IReadOnlyList<ClipboardMissionReward> Rewards);

/// <summary>The location row next to "Report to &lt;agent&gt;" is never read: the agent name alone is the resolving key (ET-172 sub 1), and the location text is free-form prose no parser should trust.</summary>
public static partial class ClipboardMissionParser
{
    private const string ObjectivesHeaderSuffix = " Objectives";
    private const string ReportToPrefix = "Report to ";

    // "<qty> × <item>" is not a form ET has ever captured; it is EVE Journal's own regex input, measured against
    // their source during ET-172's grooming, not a live client here.
    [GeneratedRegex(@"^(?<qty>\d+)\s*×\s*(?<name>.+)$")]
    private static partial Regex ItemRewardPattern();

    [GeneratedRegex(@"within (?<hours>\d+) hours?")]
    private static partial Regex BonusWindowPattern();

    public static ClipboardMissionCapture? Parse(string text)
    {
        string? objectivesHeaderName = null;
        string? agentName = null;
        int? bonusWindowSeconds = null;
        var rewards = new List<ClipboardMissionReward>();
        var block = RewardBlock.None;
        var isFirstLine = true;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (isFirstLine)
            {
                isFirstLine = false;
                var header = line.Trim();
                if (header.Length > ObjectivesHeaderSuffix.Length && header.EndsWith(ObjectivesHeaderSuffix, StringComparison.Ordinal))
                    objectivesHeaderName = header[..^ObjectivesHeaderSuffix.Length];

                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

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
                rewards.Add(ParseReward(LastTabField(line), block == RewardBlock.Rewards ? RunParameterKey.Isk : RunParameterKey.BonusIsk));
                continue;
            }

            if (block == RewardBlock.BonusRewards && bonusWindowSeconds is null)
            {
                var match = BonusWindowPattern().Match(line);
                if (match.Success && ClipboardInventoryParser.TryParseWholeNumber(match.Groups["hours"].Value, out var hours))
                    bonusWindowSeconds = (int)(hours * 3600);
            }
        }

        return objectivesHeaderName is null && agentName is null && rewards.Count == 0 && bonusWindowSeconds is null
            ? null
            : new ClipboardMissionCapture(objectivesHeaderName, agentName, bonusWindowSeconds, rewards);
    }

    private static ClipboardMissionReward ParseReward(string value, RunParameterKey iskKind)
    {
        var trimmed = value.Trim();

        if (trimmed.EndsWith(" ISK", StringComparison.Ordinal)
            && ClipboardInventoryParser.TryParseLocalNumber(trimmed[..^" ISK".Length].TrimEnd(), out var amount))
            return new ClipboardMissionReward(iskKind, amount, null, null);

        // No real capture has ever shown a loyalty-point reward line; "<n> Loyalty Points" follows the ISK line's own
        // shape (a localized number plus a literal suffix) since that is the only reward text this project has ever
        // measured — treat this as an assumption, not a second measured form.
        if (trimmed.EndsWith(" Loyalty Points", StringComparison.Ordinal)
            && ClipboardInventoryParser.TryParseLocalNumber(trimmed[..^" Loyalty Points".Length].TrimEnd(), out var loyaltyPoints))
            return new ClipboardMissionReward(RunParameterKey.LoyaltyPoints, loyaltyPoints, null, null);

        var itemMatch = ItemRewardPattern().Match(trimmed);
        if (itemMatch.Success && ClipboardInventoryParser.TryParseWholeNumber(itemMatch.Groups["qty"].Value, out var quantity))
            return new ClipboardMissionReward(RunParameterKey.Item, null, itemMatch.Groups["name"].Value.Trim(), quantity);

        return new ClipboardMissionReward(null, null, null, null);
    }

    private static string LastTabField(string line)
    {
        var fields = line.Split('\t');
        return fields[^1].Trim();
    }

    private enum RewardBlock { None, Rewards, BonusRewards }
}
