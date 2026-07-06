using System;
using System.Collections.ObjectModel;
using EveUtils.Client.Esi;
using EveUtils.Client.Imaging;
using EveUtils.Shared.App;

namespace EveUtils.Client.ViewModels;

/// <summary>An inspiration credit: a project name and the URL it links to.</summary>
public sealed record InspirationLink(string Name, string Url);

/// <summary>
/// Backs the About window: the app identity + version, the creators (with hex ESI portraits), the
/// projects we drew inspiration from, the AGPLv3 license and the mandatory CCP attribution disclaimer. Pure data
/// plus a best-effort portrait load — no app state is touched.
/// </summary>
public sealed class AboutViewModel : ViewModelBase
{
    // The creators' EVE character ids — they drive both the name and portrait resolve, so name + face always match.
    private const int RaymondKrahCharacterId = 883434905;
    private const int JithranCharacterId = 90250177;

    public string AppName => "EVE Together";
    public string Version { get; }
    public string Tagline => "A local-first, open-source tooling suite for EVE Online.";

    public string RepositoryUrl => "https://github.com/EveTogether/EveTogether";
    public string LicenseLabel => "Licensed under the GNU Affero General Public License v3.";
    public string LicenseUrl => "https://www.gnu.org/licenses/agpl-3.0.html";
    public string Copyright => $"© {DateTime.UtcNow.Year} RaymondKrah & Jithran";

    // Verbatim CCP disclaimer (Notes.md §Legal) — required wherever EVE/CCP material is shown.
    public string Disclaimer =>
        "Material related to EVE-Online is used with limited permission of CCP Games hf by using official Toolkit. " +
        "No official affiliation or endorsement by CCP Games hf is stated or implied.";

    public ObservableCollection<CreatorRowViewModel> Creators { get; }
    public ObservableCollection<InspirationLink> Inspirations { get; }

    public AboutViewModel() : this(null, null) { }

    public AboutViewModel(ICharacterPortraitProvider? portraits, ICharacterInfoService? characterInfo)
    {
        Version = $"v{AppInfo.Version}";

        // Shuffled per view so no creator is permanently listed first — neither is "the" lead.
        CreatorRowViewModel[] creators =
        [
            new("RaymondKrah", "Creator", "https://github.com/RaymondKrah", RaymondKrahCharacterId),
            new("Jithran", "Creator", "https://github.com/Jithran", JithranCharacterId)
        ];
        Random.Shared.Shuffle(creators);
        Creators = [.. creators];

        Inspirations =
        [
            new InspirationLink("eveship.fit", "https://eveship.fit/"),
            new InspirationLink("pyfa", "https://github.com/pyfa-org/Pyfa"),
            new InspirationLink("EVE Workbench", "https://www.eveworkbench.com/")
        ];

        if (portraits is not null)
            foreach (var creator in Creators)
                _ = creator.LoadAsync(portraits, characterInfo);
    }
}
