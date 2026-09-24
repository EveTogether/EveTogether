using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-278: the gamelog is read in the client's own language. Every non-English line below is SYNTHETIC: it is the
/// English line rebuilt from CCP's own message templates for that language (the localization data shipped with the
/// game client), dressed with the same markup a real English line has. No real non-English gamelog line could be
/// found publicly to confirm the shapes, so nothing here has been seen in a real file — that is a known limitation.
/// The English column is real-shaped (the same lines the other parser tests use).
/// </summary>
public class GamelogLanguageParsingTests
{
    private const string Out = "<color=0xff00ffff>";
    private const string In = "<color=0xffcc0000>";
    private const string Dim = "<color=0x77ffffff>";

    // One scenario, three languages: the same events in the same order, worded by each client.
    private static readonly string[] English =
    [
        $"[ 2026.09.24 10:00:01 ] (combat) {Out}<b>124</b> {Dim}<font size=10>to</font> <b><color=0xffffffff>Tower Sentry Bloodraider I</b><font size=10>{Dim} - Imperial Navy Acolyte - Penetrates</font>",
        $"[ 2026.09.24 10:00:02 ] (combat) {Out}<b>1192</b> {Dim}<font size=10>to</font> <b><color=0xffffffff>Offertory Sigil</b><font size=10>{Dim} - Nova Rage Heavy Assault Missile - Hits</font>",
        $"[ 2026.09.24 10:00:03 ] (combat) {In}<b>30</b> {Dim}<font size=10>from</font> <b><color=0xffffffff>Tower Sentry Bloodraider I</b><font size=10>{Dim} - Grazes</font>",
        "[ 2026.09.24 10:00:04 ] (combat) Your group of Mega Pulse Laser II misses Shadow's Wingman completely - Mega Pulse Laser II",
        "[ 2026.09.24 10:00:05 ] (combat) Your Acolyte II misses Shadow's Wingman completely - Acolyte II",
        "[ 2026.09.24 10:00:06 ] (combat) Corpii Herald misses you completely",
        "[ 2026.09.24 10:00:07 ] (combat) Moso Itonula misses you completely - Gatling Pulse Laser I",
        "[ 2026.09.24 10:00:08 ] (combat) <color=0xffccff66><b>466</b><color=0x77ffffff><font size=10> remote armor repaired to </font><b><color=0xffffffff>Fedo [TEST] Osprey | HoS - Shield</b><color=0x77ffffff><font size=10> - Medium Remote Armor Repairer II</font>",
        "[ 2026.09.24 10:00:09 ] (combat) <color=0xffccff66><b>488</b><color=0x77ffffff><font size=10> remote shield boosted by </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Medium Murky Compact Remote Shield Booster</font>",
        "[ 2026.09.24 10:00:10 ] (combat) <color=0xffccff66><b>366</b><color=0x77ffffff><font size=10> remote capacitor transmitted by </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Corpum C-Type Medium Remote Capacitor Transmitter</font>",
        "[ 2026.09.24 10:00:11 ] (combat) <color=0xffe57f7f><b>295 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Sanguinary Ashimmu</b><color=0x77ffffff><font size=10> - Sanguinary Ashimmu</font>",
        "[ 2026.09.24 10:00:12 ] (combat) <color=0xff7fffff><b>50 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Sharouran Hemah</b><color=0x77ffffff><font size=10> - Small Infectious Scoped Energy Neutralizer</font>",
        "[ 2026.09.24 10:00:13 ] (mining) <color=0x77ffffff>You mined <font size=12><color=#ff8dc169>623<color=0x77ffffff><font size=10> units of <color=0xffffffff><font size=12>Veldspar II-Grade",
        "[ 2026.09.24 10:00:14 ] (mining) <color=#fff0ff45>Critical mining success!<color=0x77ffffff><font size=10> You mined an additional <color=#fff0ff45><font size=12>1797<color=0x77ffffff><font size=10> units of <color=0xffffffff><font size=12>Raspite X-Grade",
        "[ 2026.09.24 10:00:15 ] (mining) <color=0x77ffffff>Additional <font size=12><color=#ffff454b>623<color=0x77ffffff><font size=10> units depleted from asteroid as residue",
        "[ 2026.09.24 10:00:16 ] (bounty) <font size=12><b><color=0xff00aa00>67,500 ISK</b><color=0x77ffffff> added to next bounty payout",
        "[ 2026.09.24 10:00:17 ] (None) Jumping from Rancer to Hykkota",
        "[ 2026.09.24 10:00:18 ] (None) Undocking from Jita IV - Moon 4 - Caldari Navy Assembly Plant to Jita solar system."
    ];

    private static readonly string[] German =
    [
        $"[ 2026.09.24 10:00:01 ] (combat) {Out}<b>124</b> {Dim}<font size=10>nach</font> <b><color=0xffffffff>Tower Sentry Bloodraider I</b><font size=10>{Dim} - Imperial Navy Acolyte - Einschlag</font>",
        $"[ 2026.09.24 10:00:02 ] (combat) {Out}<b>1192</b> {Dim}<font size=10>nach</font> <b><color=0xffffffff>Offertory Sigil</b><font size=10>{Dim} - Nova Rage Heavy Assault Missile - Treffer</font>",
        $"[ 2026.09.24 10:00:03 ] (combat) {In}<b>30</b> {Dim}<font size=10>von</font> <b><color=0xffffffff>Tower Sentry Bloodraider I</b><font size=10>{Dim} - Leichter Streifschuss</font>",
        "[ 2026.09.24 10:00:04 ] (combat) Ihre Mega Pulse Laser II-Gruppe hat Shadow's Wingman völlig verfehlt - Mega Pulse Laser II",
        "[ 2026.09.24 10:00:05 ] (combat) Ihr Acolyte II hat Shadow's Wingman völlig verfehlt - Acolyte II",
        "[ 2026.09.24 10:00:06 ] (combat) Corpii Herald verfehlt Sie völlig",
        "[ 2026.09.24 10:00:07 ] (combat) Moso Itonula verfehlt Sie völlig - Gatling Pulse Laser I",
        "[ 2026.09.24 10:00:08 ] (combat) <color=0xffccff66><b>466</b><color=0x77ffffff><font size=10> Panzerungs-Fernreparatur zu </font><b><color=0xffffffff>Fedo [TEST] Osprey | HoS - Shield</b><color=0x77ffffff><font size=10> - Medium Remote Armor Repairer II</font>",
        "[ 2026.09.24 10:00:09 ] (combat) <color=0xffccff66><b>488</b><color=0x77ffffff><font size=10> Schildfernbooster aktiviert von </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Medium Murky Compact Remote Shield Booster</font>",
        "[ 2026.09.24 10:00:10 ] (combat) <color=0xffccff66><b>366</b><color=0x77ffffff><font size=10> Fernenergiespeicher übertragen von </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Corpum C-Type Medium Remote Capacitor Transmitter</font>",
        "[ 2026.09.24 10:00:11 ] (combat) <color=0xffe57f7f><b>-295 GJ</b><color=0x77ffffff><font size=10> Energie neutralisiert </font><b><color=0xffffffff>Sanguinary Ashimmu</b><color=0x77ffffff><font size=10> - Sanguinary Ashimmu</font>",
        "[ 2026.09.24 10:00:12 ] (combat) <color=0xff7fffff><b>50 GJ</b><color=0x77ffffff><font size=10> Energie neutralisiert </font><b><color=0xffffffff>Sharouran Hemah</b><color=0x77ffffff><font size=10> - Small Infectious Scoped Energy Neutralizer</font>",
        "[ 2026.09.24 10:00:13 ] (mining) <color=0x77ffffff>Sie haben <font size=12><color=#ff8dc169>623<color=0x77ffffff><font size=10> Einheiten <color=0xffffffff><font size=12>Veldspar II-Grade abgebaut",
        "[ 2026.09.24 10:00:14 ] (mining) <color=#fff0ff45>Kritischer Bergbauerfolg!<color=0x77ffffff><font size=10> Sie haben zusätzliche <color=#fff0ff45><font size=12>1797<color=0x77ffffff><font size=10> Einheiten von <color=0xffffffff><font size=12>Raspite X-Grade abgebaut",
        "[ 2026.09.24 10:00:15 ] (mining) <color=0x77ffffff>Zusätzliche <font size=12><color=#ffff454b>623<color=0x77ffffff><font size=10> Einheiten aus Asteroid als Rückstände erschöpft",
        "[ 2026.09.24 10:00:16 ] (bounty) <font size=12><b><color=0xff00aa00>67.500 ISK</b><color=0x77ffffff> zur nächsten Kopfgeldzahlung hinzugefügt",
        "[ 2026.09.24 10:00:17 ] (None) Springe von Rancer nach Hykkota",
        "[ 2026.09.24 10:00:18 ] (None) Abdocken von Jita IV - Moon 4 - Caldari Navy Assembly Plant zum Sonnensystem Jita."
    ];

    // The thousands separator here is a no-break space: how the Russian client groups is unconfirmed, so this only
    // shows the parser tolerating it.
    private static readonly string[] Russian =
    [
        $"[ 2026.09.24 10:00:01 ] (combat) {Out}<b>124</b> {Dim}<font size=10>на</font> <b><color=0xffffffff>Tower Sentry Bloodraider I</b><font size=10>{Dim} - Imperial Navy Acolyte - Пробил</font>",
        $"[ 2026.09.24 10:00:02 ] (combat) {Out}<b>1192</b> {Dim}<font size=10>на</font> <b><color=0xffffffff>Offertory Sigil</b><font size=10>{Dim} - Nova Rage Heavy Assault Missile - Попал</font>",
        $"[ 2026.09.24 10:00:03 ] (combat) {In}<b>30</b> {Dim}<font size=10>из</font> <b><color=0xffffffff>Tower Sentry Bloodraider I</b><font size=10>{Dim} - Царапнул</font>",
        "[ 2026.09.24 10:00:04 ] (combat) Ваша группа Mega Pulse Laser II промахнулась мимо Shadow's Wingman - Mega Pulse Laser II",
        "[ 2026.09.24 10:00:05 ] (combat) Ваше орудие Acolyte II промахнулось мимо Shadow's Wingman - Acolyte II",
        "[ 2026.09.24 10:00:06 ] (combat) Corpii Herald: промах мимо вашего корабля",
        "[ 2026.09.24 10:00:07 ] (combat) Moso Itonula: промах мимо вашего корабля - Gatling Pulse Laser I",
        "[ 2026.09.24 10:00:08 ] (combat) <color=0xffccff66><b>466</b><color=0x77ffffff><font size=10> единиц запаса прочности брони отремонтировано </font><b><color=0xffffffff>Fedo [TEST] Osprey | HoS - Shield</b><color=0x77ffffff><font size=10> - Medium Remote Armor Repairer II</font>",
        "[ 2026.09.24 10:00:09 ] (combat) <color=0xffccff66><b>488</b><color=0x77ffffff><font size=10> единиц запаса прочности щитов получено накачкой от </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Medium Murky Compact Remote Shield Booster</font>",
        "[ 2026.09.24 10:00:10 ] (combat) <color=0xffccff66><b>366</b><color=0x77ffffff><font size=10> единиц запаса энергии накопителя получено от </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Corpum C-Type Medium Remote Capacitor Transmitter</font>",
        "[ 2026.09.24 10:00:11 ] (combat) <color=0xffe57f7f><b>295 ГДж</b><color=0x77ffffff><font size=10> энергии нейтрализовано </font><b><color=0xffffffff>Sanguinary Ashimmu</b><color=0x77ffffff><font size=10> - Sanguinary Ashimmu</font>",
        "[ 2026.09.24 10:00:12 ] (combat) <color=0xff7fffff><b>50 ГДж</b><color=0x77ffffff><font size=10> энергии нейтрализовано </font><b><color=0xffffffff>Sharouran Hemah</b><color=0x77ffffff><font size=10> - Small Infectious Scoped Energy Neutralizer</font>",
        "[ 2026.09.24 10:00:13 ] (mining) <color=0x77ffffff>Вы добыли <font size=12><color=#ff8dc169>623<color=0x77ffffff><font size=10> ед. ресурса <color=0xffffffff><font size=12>Veldspar II-Grade",
        "[ 2026.09.24 10:00:14 ] (mining) <color=#fff0ff45>Успешный крит. удар!<color=0x77ffffff><font size=10> Вы добыли ещё <color=#fff0ff45><font size=12>1797<color=0x77ffffff><font size=10> ед. ресурса <color=0xffffffff><font size=12>Raspite X-Grade",
        "[ 2026.09.24 10:00:15 ] (mining) <color=0x77ffffff>Ещё <font size=12><color=#ffff454b>623<color=0x77ffffff><font size=10> ед. превратилось в отходы",
        "[ 2026.09.24 10:00:16 ] (bounty) <font size=12><b><color=0xff00aa00>67 500 ISK</b><color=0x77ffffff> добавлено к следующей выплате вознаграждения",
        "[ 2026.09.24 10:00:17 ] (None) Осуществляется прыжок из Rancer в Hykkota",
        "[ 2026.09.24 10:00:18 ] (None) Выход из дока Jita IV - Moon 4 - Caldari Navy Assembly Plant в звездную систему Jita."
    ];

    private static List<GameLogEvent> ParseAll(string[] lines, GamelogLanguage language) =>
        [.. lines.Select(line => LogLineParser.Parse(line, language) ?? throw new InvalidOperationException($"Not parsed: {line}"))];

    [Fact]
    public void English_ReadsTheReferenceScenario()
    {
        List<GameLogEvent> events = ParseAll(English, GamelogLanguage.English);

        Assert.Equal(English.Length, events.Count);

        CombatEvent turret = Assert.IsType<CombatEvent>(events[0]);
        Assert.Equal((DamageDirection.Outgoing, 124, "Tower Sentry Bloodraider I", "Imperial Navy Acolyte", HitQuality.Penetrates),
            (turret.Direction, turret.Amount, turret.Target, turret.Weapon, turret.Quality));

        CombatEvent incoming = Assert.IsType<CombatEvent>(events[2]);
        Assert.Equal((DamageDirection.Incoming, 30, (string?)null, HitQuality.Grazes),
            (incoming.Direction, incoming.Amount, incoming.Weapon, incoming.Quality));

        RemoteRepEvent rep = Assert.IsType<RemoteRepEvent>(events[7]);
        Assert.Equal((true, 466, "armor", "Fedo [TEST] Osprey | HoS - Shield"), (rep.Outgoing, rep.Amount, rep.Kind, rep.Counterparty));

        Assert.False(Assert.IsType<NeutEvent>(events[10]).Outgoing);
        Assert.True(Assert.IsType<NeutEvent>(events[11]).Outgoing);
        Assert.Equal(1797, Assert.IsType<MiningEvent>(events[13]).Units);
        Assert.Equal(67_500, Assert.IsType<BountyEvent>(events[15]).Isk);
        Assert.Equal("Hykkota", Assert.IsType<LocationEvent>(events[16]).System);
        Assert.Equal("Jita", Assert.IsType<LocationEvent>(events[17]).System);
    }

    [Theory]
    [InlineData(GamelogLanguage.German)]
    [InlineData(GamelogLanguage.Russian)]
    public void Scenario_GivesTheSameEvents_AsItsEnglishCounterpart(GamelogLanguage language)
    {
        string[] lines = language == GamelogLanguage.German ? German : Russian;

        Assert.Equal(ParseAll(English, GamelogLanguage.English), ParseAll(lines, language));
    }

    [Theory]
    [InlineData(GamelogLanguage.German, "Ihr <b>Mining Foreman Burst II</b> gewährt <b>4</b> Flottenmitgliedern einen Bonus.")]
    [InlineData(GamelogLanguage.Russian, "Преимущества от вашего модуля <b>Mining Foreman Burst II</b> выданы <b>4</b> пилотам флота.")]
    [InlineData(GamelogLanguage.English, "Your <b>Mining Foreman Burst II</b> has applied bonuses to <b>4</b> fleet members.")]
    public void MiningBoostNotice_IsRead_InEachLanguage(GamelogLanguage language, string body)
    {
        NotifyEvent notify = Assert.IsType<NotifyEvent>(LogLineParser.Parse($"[ 2026.09.24 10:00:00 ] (notify) {body}", language));

        Assert.True(GamelogNotices.TryReadMiningBoost(notify.Message, notify.Language, out string module, out int count));
        Assert.Equal(("Mining Foreman Burst II", 4), (module, count));
    }

    [Theory]
    [InlineData(GamelogLanguage.English, "Modulated Strip Miner II deactivates as it finds the resource it was harvesting a pale shadow of its former glory.")]
    [InlineData(GamelogLanguage.German, "Modulated Strip Miner II schaltet ab, da die abgebaute Materie nur noch ein ausgesaugter Schatten dessen ist, was sie einmal war.")]
    [InlineData(GamelogLanguage.Russian, "Modulated Strip Miner II деактивируется, так как добываемый им ресурс обращен в пыль.")]
    public void ResourceDepletedNotice_IsRead_InEachLanguage(GamelogLanguage language, string message)
    {
        Assert.True(GamelogNotices.IsResourceDepleted(message, language));
        Assert.False(GamelogNotices.IsResourceDepleted("Your Modulated Strip Miner II has completed operations.", language));
    }

    [Fact]
    public void GermanLine_IsNotReadAsEnglish_AndTheOtherWayRound()
    {
        Assert.Null(LogLineParser.Parse(German[0], GamelogLanguage.English));
        Assert.Null(LogLineParser.Parse(English[0], GamelogLanguage.German));
    }

    [Fact]
    public void UnknownLanguage_ReadsNothing()
    {
        Assert.Null(LogLineParser.Parse(English[0], GamelogLanguage.Unknown));
    }

    // fr, es, ja and zh: SYNTHETIC only, one line per shape, rebuilt from the same CCP templates.
    [Theory]
    [InlineData(GamelogLanguage.French, "[ 2026.09.24 10:00:01 ] (combat) <color=0xff00ffff><b>124</b> à Tower Sentry - Acolyte II - Pénètre", "out 124 Tower Sentry / Acolyte II / Penetrates")]
    [InlineData(GamelogLanguage.French, "[ 2026.09.24 10:00:02 ] (combat) Votre Acolyte II a complètement manqué Frigate - Acolyte II", "out 0 Frigate / Acolyte II / Misses")]
    [InlineData(GamelogLanguage.French, "[ 2026.09.24 10:00:03 ] (combat) <color=0xffe57f7f>295 GJ d'énergie neutralisée aux dépens de Ashimmu - Ashimmu", "neut in 295")]
    [InlineData(GamelogLanguage.French, "[ 2026.09.24 10:00:04 ] (combat) <color=0xff7fffff>50 GJ d'énergie neutralisée en faveur de Hemah - Neutralizer", "neut out 50")]
    [InlineData(GamelogLanguage.French, "[ 2026.09.24 10:00:05 ] (mining) Vous avez extrait 623 unités de Veldspar", "mined 623 Veldspar")]
    [InlineData(GamelogLanguage.Spanish, "[ 2026.09.24 10:00:01 ] (combat) <color=0xffcc0000>30 de Tower Sentry - Roza", "in 30 Tower Sentry / - / Grazes")]
    [InlineData(GamelogLanguage.Spanish, "[ 2026.09.24 10:00:02 ] (combat) 488 de escudo remoto potenciado por HotSprockets - Booster", "rep in 488 shield HotSprockets")]
    [InlineData(GamelogLanguage.Spanish, "[ 2026.09.24 10:00:03 ] (mining) Has extraído 623 unidades de Veldspar", "mined 623 Veldspar")]
    [InlineData(GamelogLanguage.Spanish, "[ 2026.09.24 10:00:04 ] (None) Saltando de Rancer a Hykkota", "location Hykkota")]
    [InlineData(GamelogLanguage.Chinese, "[ 2026.09.24 10:00:01 ] (combat) <color=0xff00ffff>124对Tower Sentry - Acolyte II - 穿透", "out 124 Tower Sentry / Acolyte II / Penetrates")]
    [InlineData(GamelogLanguage.Chinese, "[ 2026.09.24 10:00:02 ] (combat) 466远程装甲维修量至Fedo - Repairer", "rep out 466 armor Fedo")]
    [InlineData(GamelogLanguage.Chinese, "[ 2026.09.24 10:00:03 ] (mining) 你挖掘到623单位的Veldspar", "mined 623 Veldspar")]
    [InlineData(GamelogLanguage.Japanese, "[ 2026.09.24 10:00:01 ] (combat) <color=0xff00ffff>124から Tower Sentry - Acolyte II - 小破", "out 124 Tower Sentry / Acolyte II / Penetrates")]
    [InlineData(GamelogLanguage.Japanese, "[ 2026.09.24 10:00:02 ] (combat) <color=0xffcc0000>30から Tower Sentry - 擦過", "in 30 Tower Sentry / - / Grazes")]
    [InlineData(GamelogLanguage.Japanese, "[ 2026.09.24 10:00:03 ] (combat) 466リモートアーマーリペアを Fedo - Repairerに与えました", "rep out 466 armor Fedo")]
    [InlineData(GamelogLanguage.Japanese, "[ 2026.09.24 10:00:04 ] (combat) 466リモートアーマーリペアを Fedo - Repairerから受けました", "rep in 466 armor Fedo")]
    [InlineData(GamelogLanguage.Japanese, "[ 2026.09.24 10:00:05 ] (None) Rancerから Hykkotaへジャンプ中", "location Hykkota")]
    public void OtherLanguages_ReadTheirSyntheticLines(GamelogLanguage language, string line, string expected)
    {
        GameLogEvent? parsed = LogLineParser.Parse(line, language);

        Assert.Equal(expected, Describe(parsed));
    }

    [Theory]
    [InlineData("67,500", 67_500)]
    [InlineData("67.500", 67_500)]
    [InlineData("67 500", 67_500)]
    [InlineData("67 500", 67_500)]
    [InlineData("67'500", 67_500)]
    [InlineData("1.234.567", 1_234_567)]
    [InlineData("4875", 4_875)]
    [InlineData("1.234,56", 1_234)]
    public void Bounty_ToleratesEveryCommonThousandsSeparator_InGerman(string amount, long expected)
    {
        string line = $"[ 2026.09.24 10:00:16 ] (bounty) <b><color=0xff00aa00>{amount} ISK</b><color=0x77ffffff> zur nächsten Kopfgeldzahlung hinzugefügt";

        Assert.Equal(expected, Assert.IsType<BountyEvent>(LogLineParser.Parse(line, GamelogLanguage.German)).Isk);
    }

    [Fact]
    public void Bounty_IsStillRead_WhenTheCurrencyIsNotWrittenOut()
    {
        string line = "[ 2026.09.24 10:00:16 ] (bounty) 67.500 zur nächsten Kopfgeldzahlung hinzugefügt";

        Assert.Equal(67_500, Assert.IsType<BountyEvent>(LogLineParser.Parse(line, GamelogLanguage.German)).Isk);
    }

    [Fact]
    public void JapaneseDamage_HasNoWordForDirection_SoItFollowsTheLineColour()
    {
        CombatEvent outgoing = Assert.IsType<CombatEvent>(LogLineParser.Parse(
            "[ 2026.09.24 10:00:01 ] (combat) <color=0xff00ffff>124から Tower Sentry - Acolyte II - 小破", GamelogLanguage.Japanese));
        CombatEvent incoming = Assert.IsType<CombatEvent>(LogLineParser.Parse(
            "[ 2026.09.24 10:00:02 ] (combat) <color=0xffcc0000>30から Tower Sentry - 擦過", GamelogLanguage.Japanese));

        Assert.Equal((DamageDirection.Outgoing, DamageDirection.Incoming), (outgoing.Direction, incoming.Direction));
        Assert.Null(LogLineParser.Parse("[ 2026.09.24 10:00:03 ] (combat) 30から Tower Sentry - 擦過", GamelogLanguage.Japanese));
    }

    [Fact]
    public void EveryLanguage_BuildsItsGrammar()
    {
        foreach (GamelogLanguage language in Enum.GetValues<GamelogLanguage>().Where(language => language != GamelogLanguage.Unknown))
        {
            Assert.NotNull(LogLineParser.Parse("[ 2026.09.24 10:00:00 ] (notify) anything", language));
        }
    }

    private static string Describe(GameLogEvent? parsed) => parsed switch
    {
        CombatEvent { Quality: HitQuality.Misses } miss => $"{(miss.Direction == DamageDirection.Outgoing ? "out" : "in")} {miss.Amount} {miss.Target} / {miss.Weapon ?? "-"} / {miss.Quality}",
        CombatEvent hit => $"{(hit.Direction == DamageDirection.Outgoing ? "out" : "in")} {hit.Amount} {hit.Target} / {hit.Weapon ?? "-"} / {hit.Quality}",
        RemoteRepEvent rep => $"rep {(rep.Outgoing ? "out" : "in")} {rep.Amount} {rep.Kind} {rep.Counterparty}",
        NeutEvent neut => $"neut {(neut.Outgoing ? "out" : "in")} {neut.Amount}",
        MiningEvent mined => $"mined {mined.Units} {mined.OreType}",
        LocationEvent location => $"location {location.System}",
        _ => "null"
    };
}
