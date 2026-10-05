using EveUtils.Client.Skills;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Plans.Commands;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>
/// COPY AS TEXT / IMPORT FROM TEXT (ET-355 AC5): one "Skill N" per line, level as a roman or arabic numeral. An
/// unrecognised line is reported rather than silently dropped, so a plan never loses a row without the pilot knowing.
/// </summary>
public static class SkillPlanTextCodec
{
    public static string ToText(IReadOnlyList<(int SkillTypeId, int Level)> rows, ISdeAccessor sde) =>
        string.Join('\n', rows.Select(row => $"{_NameOf(row.SkillTypeId, sde)} {RomanLevel.Text(row.Level)}"));

    public static SkillPlanTextParseResult Parse(string text, ISdeAccessor sde)
    {
        var rows = new List<SkillPlanRowDraft>();
        var unrecognized = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var parsed = _ParseLine(line, sde);
            if (parsed is null)
            {
                unrecognized.Add(line);
            }
            else
            {
                rows.Add(parsed);
            }
        }

        return new SkillPlanTextParseResult(rows, unrecognized);
    }

    private static SkillPlanRowDraft? _ParseLine(string line, ISdeAccessor sde)
    {
        int lastSpace = line.LastIndexOf(' ');
        if (lastSpace <= 0)
        {
            return null;
        }

        string name = line[..lastSpace].Trim();
        int? level = _ParseLevel(line[(lastSpace + 1)..].Trim());
        if (level is null || !sde.TryGetTypeId(name, out int typeId))
        {
            return null;
        }

        return new SkillPlanRowDraft(typeId, level.Value, null);
    }

    private static int? _ParseLevel(string token) => token.ToUpperInvariant() switch
    {
        "I" or "1" => 1,
        "II" or "2" => 2,
        "III" or "3" => 3,
        "IV" or "4" => 4,
        "V" or "5" => 5,
        _ => null
    };

    private static string _NameOf(int typeId, ISdeAccessor sde) =>
        sde.TryGetTypeName(typeId, out string name) ? name : $"Type {typeId}";
}

/// <param name="Unrecognized">Every line that named no known skill or level, verbatim — shown to the pilot rather
/// than dropped (ET-355 AC5).</param>
public sealed record SkillPlanTextParseResult(IReadOnlyList<SkillPlanRowDraft> Rows, IReadOnlyList<string> Unrecognized);
