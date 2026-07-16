using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AzerothCoreControl.App.Behaviors;

/// <summary>
/// Attached behavior that keeps an <see cref="ItemsControl"/> (e.g. the console ListBox) scrolled to the
/// newest item as rows are appended — but only when the user is already near the bottom, so scrolling up
/// to read history isn't yanked back down. Scroll requests are coalesced so a burst of thousands of
/// appended lines schedules a single scroll instead of flooding the dispatcher (which would freeze the UI).
/// </summary>
public static class AutoScrollBehavior
{
    // Tracks whether a scroll is already queued for a given ListBox (coalescing).
    private static readonly ConditionalWeakTable<ListBox, StrongBox<bool>> ScrollPending = new();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
            return;

        if ((bool)e.NewValue && list.Items is INotifyCollectionChanged incc)
            incc.CollectionChanged += (_, args) => OnCollectionChanged(list, args);
    }

    private static void OnCollectionChanged(ListBox list, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
            return;

        var pending = ScrollPending.GetValue(list, _ => new StrongBox<bool>(false));
        if (pending.Value)
            return; // a scroll is already queued — coalesce into it

        pending.Value = true;
        // Background priority: runs after the pending item adds/layout, once per burst.
        list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            pending.Value = false;
            if (list.Items.Count == 0)
                return;

            var scrollViewer = FindScrollViewer(list);
            var atBottom = scrollViewer == null ||
                           scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 48;
            if (atBottom)
                list.ScrollIntoView(list.Items[^1]);
        }));
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
