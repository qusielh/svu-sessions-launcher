using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SvuSessionsLauncher.Theming;

namespace SvuSessionsLauncher.Helpers;

/// <summary>
/// Makes a window's native (OS) caption follow the launcher's light/dark theme.
/// The windows that keep the standard chrome (sign-in, main, chat) have their
/// title bar drawn by Windows, and WPF exposes no managed API to recolour it, so
/// the caption is tinted through DWM. It is applied as early as possible (on
/// <see cref="Window.SourceInitialized"/>, i.e. when the HWND is created and
/// before the window's first paint, so a dark window never flashes a light
/// caption) and re-applied whenever <see cref="ThemeManager.Changed"/> fires, so
/// switching theme in the appearance window repaints the caption live.
///
/// <para>Usage (XAML): <c>helpers:WindowTitleBar.ThemeNativeCaption="True"</c>.</para>
/// </summary>
public static class WindowTitleBar
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE. 20 is the released value; 19 was the
    // pre-Windows-10-20H1 value and is used as a fallback.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModePre20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Used ONLY after a minimize -> restore, to ask DWM to redraw the existing
    // non-client frame (caption + the Windows 11 rounded corners). This is NOT
    // SetWindowPos(SWP_FRAMECHANGED): that tears the frame down and rebuilds it,
    // which is what shattered the corners. RedrawWindow just re-composites the
    // frame DWM already has.
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRgn, uint flags);

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint RdwUpdateNow = 0x0100;

    public static readonly DependencyProperty ThemeNativeCaptionProperty =
        DependencyProperty.RegisterAttached(
            "ThemeNativeCaption",
            typeof(bool),
            typeof(WindowTitleBar),
            new PropertyMetadata(false, OnThemeNativeCaptionChanged));

    public static void SetThemeNativeCaption(DependencyObject element, bool value) =>
        element.SetValue(ThemeNativeCaptionProperty, value);

    public static bool GetThemeNativeCaption(DependencyObject element) =>
        (bool)element.GetValue(ThemeNativeCaptionProperty);

    private static void OnThemeNativeCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
        {
            return;
        }

        // Apply at SourceInitialized (HWND creation, before the first paint), NOT
        // Loaded: Loaded runs after the window is already on screen, so Windows
        // paints a white/light caption first and only recolours it when DWM next
        // repaints -- the "starts white, goes black seconds later" bug.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Attach(window); // Window already realised: apply now.
        }
        else
        {
            window.SourceInitialized += OnSourceInitialized;
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Attach(window);
        }
    }

    private static void Attach(Window window)
    {
        Apply(window);

        void OnThemeChanged() => Apply(window);

        // DWM re-presents the window from its cached surface (without
        // re-compositing the non-client frame) whenever it comes back into view
        // after being hidden -- on a minimize -> restore AND on an Alt+Tab return.
        // In both cases the Windows 11 rounded corners land jagged until a full
        // re-composite. Redraw the frame just after it returns, in each case.
        var wasMinimized = false;
        window.StateChanged += (_, _) =>
        {
            var minimized = window.WindowState == WindowState.Minimized;
            if (wasMinimized && !minimized)
            {
                RedrawFrame(window);
            }

            wasMinimized = minimized;
        };

        // Alt+Tab (and clicking back to the window): a return to the foreground
        // fires Activated without any StateChanged, so cover it here too. Only
        // redraw when the window had actually lost activation, so a plain click
        // inside an already-active window does nothing.
        var wasDeactivated = false;
        window.Deactivated += (_, _) => wasDeactivated = true;
        window.Activated += (_, _) =>
        {
            if (wasDeactivated)
            {
                wasDeactivated = false;
                RedrawFrame(window);
            }
        };

        ThemeManager.Changed += OnThemeChanged;
        window.Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private static void RedrawFrame(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Let the restore settle first: redrawing during the transition can
            // itself land mid-composite. A Background-priority hop runs after the
            // window is actually back on screen.
            window.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    // A control left with keyboard focus after a restore keeps its
                    // focus decoration (ring/border) painted, and that adorner's
                    // edge is what read as "jagged corners". Give the window itself
                    // focus so nothing is left highlighted.
                    if (System.Windows.Input.Keyboard.FocusedElement is UIElement focused
                        && focused != window)
                    {
                        System.Windows.Input.Keyboard.ClearFocus();
                    }

                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                        RdwFrame | RdwInvalidate | RdwUpdateNow);
                }));
        }
        catch
        {
            // Redraw is best-effort; a failure just leaves the frame as-is.
        }
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = ThemeManager.Current == AppTheme.Dark ? 1 : 0;
        try
        {
            // Setting the immersive-dark-mode attribute is all that is needed to
            // recolour the native caption. DWM repaints it on the next frame
            // automatically.
            //
            // We deliberately do NOT force a frame rebuild here. An earlier version
            // called SetWindowPos(SWP_FRAMECHANGED) + RedrawWindow(RDW_FRAME) to
            // make the switch "instant", and that is exactly what shattered the
            // Windows 11 rounded corners: forcing DWM to tear down and rebuild the
            // non-client frame leaves the corner arcs aliased/jagged until the next
            // full re-composite. It also pulled the foreground around through
            // WM_NCACTIVATE. Neither is acceptable, and neither is necessary.
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModePre20H1, ref value, sizeof(int));
            }
        }
        catch
        {
            // Older Windows without the attribute simply keeps its default caption.
        }
    }
}
