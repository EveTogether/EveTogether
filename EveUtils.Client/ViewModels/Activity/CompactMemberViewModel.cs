using System.Linq;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>One pilot as a hex in the compact run window: their initials, and their name for the hover.</summary>
public sealed class CompactMemberViewModel(string name)
{
    public string Name { get; } = name;

    public string Initials { get; } = string.Concat(
        name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpperInvariant(word[0])));
}
