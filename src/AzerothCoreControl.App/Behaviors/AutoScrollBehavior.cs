using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace AzerothCoreControl.App.Behaviors;

/// <summary>
/// Keeps a <see cref="ListBox"/> (the console / log views) pinned to the newest line, while letting the user
/// scroll up to read history without being yanked back down. Scrolling up releases the pin; scrolling back to
/// the bottom re-arms it.
/// </summary>
/// <remarks>
/// This drives off the ScrollViewer's own <see cref="ScrollViewer.ScrollChanged"/> rather than the
/// collection's CollectionChanged, because that event fires as part of the layout pass that grew the extent.
/// The previous version queued a scroll at <c>DispatcherPriority.Background</c> — below Input and Render —
/// so during a startup firehose (thousands of lines) the dispatcher was busy laying out and the scroll was
/// deferred exactly when it most needed to keep up. It also compared VerticalOffset against a 48 "pixel"
/// slack, but a ListBox scrolls logically: those units are ITEMS, so "near the bottom" silently meant
/// "within 48 rows", and reading history a few lines up still snapped you to the end.
/// </remarks>
public static class AutoScrollBehavior
{
    /// <summary>Pixels of slack for "the user is at the bottom" — a hair, to absorb fractional offsets.</summary>
    private const double BottomEpsilon = 2.0;

    // Per-list: is the view currently pinned to the bottom? Starts pinned.
    private static readonly ConditionalWeakTable<ListBox, StrongBox<bool>> Pinned = new();

    // Per-list: already hooked. A TabControl unloads the content of an unselected tab and reloads it on
    // reselection, so Loaded fires again on the same ListBox — without this, every visit to the Console tab
    // would add another ScrollChanged handler to the same ScrollViewer.
    private static readonly ConditionalWeakTable<ListBox, StrongBox<bool>> Attached = new();

    // Per-list: a LayoutUpdated retry hook is already pending (see RetryWhenRealized).
    private static readonly ConditionalWeakTable<ListBox, StrongBox<bool>> Retrying = new();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || !(bool)e.NewValue)
            return;

        // The ScrollViewer lives in the control template, so it doesn't exist until the template is applied.
        if (list.IsLoaded)
            Attach(list);
        else
            list.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var list = (ListBox)sender;
        list.Loaded -= OnLoaded;
        Attach(list);
    }

    private static void Attach(ListBox list)
    {
        var attached = Attached.GetValue(list, _ => new StrongBox<bool>(false));
        if (attached.Value)
        {
            // Re-shown after a tab switch: already hooked, just catch up to whatever arrived while hidden.
            if (Pinned.GetValue(list, _ => new StrongBox<bool>(true)).Value)
                FindScrollViewer(list)?.ScrollToEnd();
            return;
        }

        if (FindScrollViewer(list) is not { } scrollViewer)
        {
            // No ScrollViewer yet: a Collapsed element (or one with a Collapsed ancestor) is skipped by
            // layout, so its template is never applied and it has no visual children. The build-report log
            // is exactly this — hidden until a build fails. Bailing here would be permanent, so wait for the
            // layout pass that realizes it. LayoutUpdated is chatty, hence unhooking on the first success.
            RetryWhenRealized(list);
            return;
        }
        attached.Value = true;

        var pinned = Pinned.GetValue(list, _ => new StrongBox<bool>(true));
        scrollViewer.ScrollChanged += (_, args) =>
        {
            // ExtentHeightChange == 0 means this scroll came from the user (wheel, drag, keyboard) rather
            // than from content arriving — that's the only time their intent should re-arm or release the pin.
            if (args.ExtentHeightChange == 0)
            {
                pinned.Value = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - BottomEpsilon;
                return;
            }

            if (pinned.Value)
                scrollViewer.ScrollToEnd();
        };

        scrollViewer.ScrollToEnd();
    }

    /// <summary>Keep looking for the ScrollViewer on each layout pass until the list is actually realized.</summary>
    private static void RetryWhenRealized(ListBox list)
    {
        var retrying = Retrying.GetValue(list, _ => new StrongBox<bool>(false));
        if (retrying.Value)
            return;
        retrying.Value = true;

        EventHandler? onLayout = null;
        onLayout = (_, _) =>
        {
            if (FindScrollViewer(list) == null)
                return;
            list.LayoutUpdated -= onLayout;
            retrying.Value = false;
            Attach(list);
        };
        list.LayoutUpdated += onLayout;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var result = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }
        return null;
    }
}
