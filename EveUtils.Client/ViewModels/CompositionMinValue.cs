namespace EveUtils.Client.ViewModels;

/// <summary>
/// Parses the optional pilot-minimum text fields in the composition editor. Blank or non-positive input means
/// "no minimum" (null), matching the "—" placeholder on the role-group and per-fit inputs.
/// </summary>
internal static class CompositionMinValue
{
    public static int? Parse(string? text) =>
        int.TryParse(text?.Trim(), out var value) && value > 0 ? value : null;

    public static string Format(int? value) => value is int v ? v.ToString() : "";
}
