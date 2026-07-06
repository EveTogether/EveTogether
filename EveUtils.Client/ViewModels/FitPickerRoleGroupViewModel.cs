using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A role group on the picker's Composition source: the doctrine's allowed fits for one role, with the role's
/// pilot requirement shown as a header (group minimum, or the per-fit breakdown). Only used when the picker is scoped
/// to a coupled composition.
/// </summary>
public sealed class FitPickerRoleGroupViewModel(string roleName, string minLabel, IEnumerable<FitPickerRowViewModel> rows)
{
    public string RoleName { get; } = roleName;

    /// <summary>"≥40", "3+2" or "—" — the same two-level requirement label the library uses.</summary>
    public string MinLabel { get; } = minLabel;

    public ObservableCollection<FitPickerRowViewModel> Rows { get; } = [.. rows];
}
