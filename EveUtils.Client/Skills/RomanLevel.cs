namespace EveUtils.Client.Skills;

/// <summary>Skill levels are shown in-game as roman numerals I-V — shared formatting for the SKILLS module (ET-16).</summary>
public static class RomanLevel
{
    public static string Text(int level) => level switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        5 => "V",
        _ => level.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
