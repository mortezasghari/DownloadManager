namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Breakpoint math for the adaptive settings layout (ADR-0024). The settings groups are laid out in a
/// <c>WrapPanel</c> of fixed-width blocks (<see cref="GroupWidth"/>): when the available width fits two
/// blocks side by side they form two columns, otherwise they reflow to one. This pure helper documents and
/// verifies that breakpoint (the actual reflow is the WrapPanel's intrinsic behavior with the same widths),
/// so the responsive behavior is headless-testable without a render.
/// </summary>
public static class SettingsLayout
{
    /// <summary>Width of one settings group block (matches the WrapPanel item width in the view).</summary>
    public const double GroupWidth = 380;

    /// <summary>Inter-block gap (WrapPanel item spacing).</summary>
    public const double Gap = 16;

    /// <summary>At/above this available width two groups fit side by side → two columns.</summary>
    public const double TwoColumnBreakpoint = (GroupWidth * 2) + Gap;

    /// <summary>Number of settings columns for a given available width: 2 when two blocks fit, else 1.</summary>
    public static int ColumnsForWidth(double availableWidth) =>
        availableWidth >= TwoColumnBreakpoint ? 2 : 1;
}