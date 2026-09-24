using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Languages;

/// <summary>Every language this build can read. Adding one is a new <c>&lt;Name&gt;Templates</c> file plus an entry here.</summary>
internal static class GamelogTemplateTables
{
    public static IReadOnlyDictionary<GamelogLanguage, GamelogTemplates> All { get; } =
        new Dictionary<GamelogLanguage, GamelogTemplates>
        {
            [GamelogLanguage.English] = EnglishTemplates.Table,
            [GamelogLanguage.German] = GermanTemplates.Table,
            [GamelogLanguage.Russian] = RussianTemplates.Table,
            [GamelogLanguage.French] = FrenchTemplates.Table,
            [GamelogLanguage.Spanish] = SpanishTemplates.Table,
            [GamelogLanguage.Japanese] = JapaneseTemplates.Table,
            [GamelogLanguage.Chinese] = ChineseTemplates.Table
        };
}
