using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace EveUtils.Client.UiTests;

/// <summary>
/// One shared answer to "what does the operator actually see" (ET-89), so every render assertion in this suite
/// uses the same definition instead of each test writing its own. <c>IsVisible="false"</c> does not remove a
/// control from the visual tree — <see cref="Visual.GetVisualDescendants"/> still returns it — so a raw
/// <c>TextBlock</c> sweep can make <c>Assert.DoesNotContain</c> wrongly red (ET-83) or, more dangerously, let
/// <c>Assert.Contains</c>/<c>Single</c>/<c>Any</c> go green on a control the operator never sees.
/// </summary>
public static class RenderedText
{
    /// <summary>
    /// "Is this shown at all" — filters the <c>IsVisible</c> chain of the whole ancestor path via
    /// <see cref="Visual.IsEffectivelyVisible"/>. The cheap default; use this unless the assertion is actually
    /// about where the text sits, not whether it renders.
    ///
    /// Measured (ET-89 grooming) to say nothing about clipping or placement: a <c>TextBlock</c> scrolled out of a
    /// <see cref="ScrollViewer"/>'s viewport, or one sitting past a window's edge, is still
    /// <c>IsEffectivelyVisible</c>. Use <see cref="OnScreenTexts"/> for any assertion whose wording is really a
    /// position claim — "not clipped", "below the fold", "pushed off the screen".
    /// </summary>
    public static List<string> VisibleTexts(Visual root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text))
            .Select(block => block.Text!)];

    /// <summary>
    /// <see cref="VisibleTexts"/>'s filter, plus non-zero bounds that land fully inside <paramref name="window"/>'s
    /// own client rect once transformed into its coordinate space. Required for any assertion whose wording
    /// contains "not clipped", "below the fold", or "pushed off the screen" — <see cref="VisibleTexts"/> cannot
    /// tell a control that renders from one that renders outside the window (ET-89 grooming measured this against
    /// two suspect tests: both needed this bounds check, not the cheaper one, to actually prove what they claimed).
    ///
    /// Still not covered here, and needing a pixel assertion against a captured frame instead:
    /// <list type="bullet">
    /// <item>content scrolled out of a <see cref="ScrollViewer"/> — its transform still lands inside the
    /// viewport's own rect, not the window's</item>
    /// <item><c>Opacity="0"</c></item>
    /// <item>a control fully covered by another control on top of it (z-order)</item>
    /// </list>
    /// </summary>
    public static List<string> OnScreenTexts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text)
                             && _IsOnScreen(block, window))
            .Select(block => block.Text!)];

    private static bool _IsOnScreen(TextBlock block, Window window)
    {
        if (block.Bounds.Width <= 0 || block.Bounds.Height <= 0)
            return false;

        var topLeft = block.TranslatePoint(new Point(0, 0), window);
        var bottomRight = block.TranslatePoint(new Point(block.Bounds.Width, block.Bounds.Height), window);
        if (topLeft is not { } tl || bottomRight is not { } br)
            return false;

        return tl.X >= 0 && tl.Y >= 0 && br.X <= window.ClientSize.Width && br.Y <= window.ClientSize.Height;
    }
}
