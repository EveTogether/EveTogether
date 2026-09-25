namespace EveUtils.Client.Skills;

/// <summary>Level pips shown for a skill (ET-16): filled for a trained level, a diamond for the level currently
/// training, hollow for the rest of I-V — matching the in-game skill sheet. Shared by the CATALOGUE row, the
/// TRAINING QUEUE row and the detail pane, so all three read the same level the same way.</summary>
public static class SkillLevelPips
{
    public const int MaxLevel = 5;

    public static string Text(int currentLevel, int? trainingLevel)
    {
        var pips = new char[MaxLevel];
        for (int i = 0; i < MaxLevel; i++)
        {
            int level = i + 1;
            pips[i] = level <= currentLevel ? '■' : level == trainingLevel ? '◆' : '□';
        }
        return new string(pips);
    }
}
