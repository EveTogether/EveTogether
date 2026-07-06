using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One of my characters shown as a member-leaf under a fleet node in the Fleets window: the
/// pilot's role, the fit they fly, a can-fly badge, and a SELECT FIT action that opens the
/// composition-scoped picker so the pilot picks their OWN fit (master-plan §5; the server authorizes owner-or-self).
/// The command is supplied by <see cref="FleetsViewModel"/>, which owns the (server, character, composition) context.
/// </summary>
public sealed partial class FleetMemberRowViewModel : ObservableObject
{
    public FleetMemberRowViewModel(
        long memberId, int characterId, string characterName, string roleLabel,
        FitReferenceInfo? assignedFit, MemberSkillBadge? skillBadge, IAsyncRelayCommand selectFitCommand,
        IAsyncRelayCommand? openFitCommand = null, IAsyncRelayCommand? leaveCommand = null, bool canLeave = false)
    {
        MemberId = memberId;
        CharacterId = characterId;
        CharacterName = characterName;
        RoleLabel = roleLabel;
        AssignedFit = assignedFit;
        SelectFitCommand = selectFitCommand;
        OpenFitCommand = openFitCommand;
        LeaveCommand = leaveCommand;
        CanLeave = canLeave;
        if (skillBadge is not null)
        {
            HasSkillBadge = true;
            CanFly = skillBadge.CanFly;
            SkillTooltip = skillBadge.Tooltip;
        }
    }

    public long MemberId { get; }
    public int CharacterId { get; }
    public string CharacterName { get; }

    /// <summary>The pilot's position in the fleet structure (FC / Wing / Squad / Unassigned), for the leaf row.</summary>
    public string RoleLabel { get; }

    /// <summary>The fit this pilot flies, or null when none is assigned yet.</summary>
    public FitReferenceInfo? AssignedFit { get; }

    public bool HasAssignedFit => AssignedFit is not null;
    public string AssignedFitName => AssignedFit?.FitName ?? "— no fit selected —";

    /// <summary>SELECT FIT when none is assigned, CHANGE FIT to replace the current one.</summary>
    public string SelectFitButtonLabel => HasAssignedFit ? "CHANGE FIT" : "SELECT FIT";

    /// <summary>can-fly verdict: no badge when there is no fit, the character's skills are not locally
    /// known, or the SDE is unavailable (unknown ≠ "can't fly").</summary>
    public bool HasSkillBadge { get; }
    public bool CanFly { get; }
    public string SkillTooltip { get; } = "";

    /// <summary>The "can fly" badge shows only when there is a verdict AND the pilot trains every required skill.</summary>
    public bool ShowCanFly => HasSkillBadge && CanFly;

    /// <summary>The "skills missing" badge shows only when there is a verdict AND at least one skill is short.</summary>
    public bool ShowSkillGap => HasSkillBadge && !CanFly;

    /// <summary>A neutral "?" shows when a fit is assigned but there is no verdict at all — neither computed locally
    /// nor reported by the pilot's client — so the gap is visible (and explained) instead of silently blank.</summary>
    public bool ShowSkillUnknown => HasAssignedFit && !HasSkillBadge;

    public string UnknownSkillTooltip =>
        "Can-fly unknown: this character's skills aren't known locally and the pilot's client hasn't reported a verdict. " +
        "Sign this character in with the read_skills scope (and import skills) to see a can-fly check.";

    /// <summary>Opens the composition-scoped single fit picker and persists the pick (owner-or-self, master-plan §5).</summary>
    public IAsyncRelayCommand SelectFitCommand { get; }

    /// <summary>Opens the read-only fit detail for the assigned fit so any member's fit can be inspected from the
    /// fleet list; null/disabled when no fit is assigned.</summary>
    public IAsyncRelayCommand? OpenFitCommand { get; }

    /// <summary>Pulls this one character out of the fleet: set only for my non-owner characters on a
    /// server fleet, so an alt I fly in a fleet I own can leave while the owner stays. Null for the owner's own
    /// character (the owner disbands/transfers instead) and for local fleets.</summary>
    public IAsyncRelayCommand? LeaveCommand { get; }

    /// <summary>Drives the leaf's LEAVE button — true for a non-owner character on a server fleet.</summary>
    public bool CanLeave { get; }

    /// <summary>The pilot's ESI portrait for the hex on the leaf row (stream B / B-3, mirrors the character column's
    /// hex portrait); null until loaded or when images are off/offline, so the leaf falls back to the initial glyph.</summary>
    [ObservableProperty] private Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;

    partial void OnPortraitChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPortrait));

    /// <summary>First letter of the name, shown in the hex when no portrait render is available (offline/disabled/external).</summary>
    public string Initial => string.IsNullOrEmpty(CharacterName) ? "?" : CharacterName[..1].ToUpperInvariant();

    /// <summary>Loads the ESI portrait best-effort (opt-in image setting); a failure leaves the glyph fallback.</summary>
    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits, CancellationToken cancellationToken = default)
    {
        if (CharacterId <= 0)
            return;
        Portrait = await portraits.GetPortraitAsync(CharacterId, 64, cancellationToken);
    }
}
