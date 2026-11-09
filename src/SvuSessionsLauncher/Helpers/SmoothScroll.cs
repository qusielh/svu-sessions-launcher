using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SvuSessionsLauncher.Helpers;

/// <summary>
/// Gives a scrollable control (ListBox/ListView/ScrollViewer) "slip on ice"
/// wheel scrolling: wheel ticks accumulate into a running target and the view
/// eases toward it, so repeated ticks glide instead of jumping one row each.
///
/// <para>Usage (XAML): <c>helpers:SmoothScroll.Enabled="True"</c>.</para>
/// </summary>
public static class SmoothScroll
{
    /// <summary>Wheel-notch distance in device-independent pixels.</summary>
    private const double NotchPixels = 110;

    /// <summary>How long the ease-to-target takes. Short enough to feel direct.</summary>
    private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(260));

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    // The value the animation drives; OnOffsetChanged pushes it into the real
    // scroll offset. Animating this (rather than setting the offset directly)
    // keeps a single continuous motion across overlapping wheel ticks.
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0d, OnOffsetChanged));

    private static void SetOffset(ScrollViewer viewer, double value) => viewer.SetValue(OffsetProperty, value);

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer viewer && e.NewValue is double offset)
        {
            viewer.ScrollToVerticalOffset(offset);
        }
    }

    // Per-ScrollViewer state, keyed off the element so each list glides on its own.
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0d));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;

        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var scrollViewer = FindScrollViewer(source);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        // A notch is 120. Move NotchPixels per notch, accumulated onto the running
        // target so fast successive ticks build up speed instead of restarting.
        var currentTarget = (double)scrollViewer.GetValue(TargetProperty);

        // Re-sync if an animation is not running (first tick, or the user dragged
        // the bar): start from where the view actually is.
        if (!scrollViewer.HasAnimatedProperties)
        {
            currentTarget = scrollViewer.VerticalOffset;
        }

        var target = currentTarget - (e.Delta / 120.0) * NotchPixels;
        target = Math.Clamp(target, 0, scrollViewer.ScrollableHeight);
        scrollViewer.SetValue(TargetProperty, target);

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = Glide,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        scrollViewer.BeginAnimation(OffsetProperty, animation);

        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
