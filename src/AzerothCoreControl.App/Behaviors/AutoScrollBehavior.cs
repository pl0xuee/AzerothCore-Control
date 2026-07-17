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
/// Driven by the ScrollViewer's own <see cref="ScrollViewer.ScrollChanged"/> rather than the collection's
/// CollectionChanged, because that event fires as part of the layout pass that grew the extent — it cannot
/// fall behind. A previous version queued a scroll at <c>DispatcherPriority.Background</c> (below Input), so
/// a startup firehose starved it exactly when it needed to keep up, and it measured "near the bottom" with a
/// 48-"pixel" slack against a ListBox that scrolls in ITEM units.
/// </remarks>
public static class AutoScrollBehavior
{
    /// <summary>Pixels of slack for "the user is at the bottom" — a hair, to absorb fractional offsets.</summary>
    private const double BottomEpsilon = 2.0;

    // Per-list: is the view currently pinned to the bottom? Starts pinned.
    private static readonly ConditionalWeakTable<ListBox, StrongBox<bool>> Pinned = new();

    // Per-list: the ScrollViewer we hooked. A TabControl unloads an unselected tab's content and reloads it
    // on reselection, so Loaded fires repeatedly on the same ListBox; without this we'd stack duplicate
    // handlers, and if the template were ever re-applied we'd be left holding a dead ScrollViewer.
    private static readonly ConditionalWeakTable<ListBox, ScrollViewer> Hooked = new();

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
        list.Loaded += OnLoaded;
        if (list.IsLoaded)
            Attach(list);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((ListBox)sender);

    private static void Attach(ListBox list)
    {
        if (FindScrollViewer(list) is not { } scrollViewer)
        {
            // No ScrollViewer yet: a Collapsed element (or one with a Collapsed ancestor) is skipped by
            // layout, so its template is never applied and it has no visual children. The build-report log is
            // exactly this — hidden until a build fails. Bailing permanently would leave it unscrolled, so
            // wait for the layout pass that realizes it.
            RetryWhenRealized(list);
            return;
        }

        var pinned = Pinned.GetValue(list, _ => new StrongBox<bool>(true));

        // Same ScrollViewer as last time (the common tab-switch case): don't hook twice, just catch up on
        // whatever arrived while this tab was hidden.
        if (Hooked.TryGetValue(list, out var existing) && ReferenceEquals(existing, scrollViewer))
        {
            if (pinned.Value)
                scrollViewer.ScrollToEnd();
            return;
        }

        Hooked.Remove(list);
        Hooked.Add(list, scrollViewer);

        scrollViewer.ScrollChanged += (_, args) => OnScrollChanged(scrollViewer, args, pinned);
        scrollViewer.ScrollToEnd();
    }

    private static void OnScrollChanged(ScrollViewer scrollViewer, ScrollChangedEventArgs args, StrongBox<bool> pinned)
    {
        // Only an unchanged extent AND an unchanged viewport mean the user themselves moved the view — that's
        // the sole signal allowed to release the pin.
        //
        // Checking the extent alone is not enough: re-showing this tab (or resizing the window) re-measures
        // the list and fires ScrollChanged with the extent unchanged but the viewport growing from 0, while
        // VerticalOffset still reads 0. That looks identical to "the user scrolled to the top", which
        // silently un-pinned the console for good — new lines kept arriving below the fold and it appeared
        // to have stopped the moment you switched tabs and came back.
        if (args.ExtentHeightChange == 0 && args.ViewportHeightChange == 0)
        {
            pinned.Value = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - BottomEpsilon;
            return;
        }

        // Content arrived, or the viewport changed shape — follow it if we're still pinned.
        if (pinned.Value)
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
