using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One character tile on the KILLMAILS overview (ET-332): a face, a name and either a killmail count or,
/// missing <c>esi-killmails.read_killmails.v1</c>, a GRANT ACCESS chip that starts the same re-authentication as
/// every other scope gap in this app.</summary>
public sealed partial class KillmailCharacterOptionViewModel : ObservableObject
{
    private readonly Action<KillmailCharacterOptionViewModel> _select;
    private readonly Func<int, Task> _grantAccess;

    public KillmailCharacterOptionViewModel(int characterId, string name, CharacterFaceViewModel face, bool needsAccess,
        Action<KillmailCharacterOptionViewModel> select, Func<int, Task> grantAccess)
    {
        CharacterId = characterId;
        Name = name;
        Face = face;
        NeedsAccess = needsAccess;
        _select = select;
        _grantAccess = grantAccess;
    }

    public int CharacterId { get; }

    public string Name { get; }

    public CharacterFaceViewModel Face { get; }

    /// <summary>True when this character never granted, or lost, the killmails scope: the overview shows GRANT ACCESS
    /// instead of a count and reads no killmails for it (ET-332 AC3).</summary>
    public bool NeedsAccess { get; }

    /// <summary>Null until this character has actually been read: shown blank rather than "0", so a tile nobody has
    /// selected yet never reads as "no kills" (ET-332).</summary>
    [ObservableProperty] private int? _count;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => _select(this);

    [RelayCommand]
    private Task GrantAccess() => _grantAccess(CharacterId);
}
