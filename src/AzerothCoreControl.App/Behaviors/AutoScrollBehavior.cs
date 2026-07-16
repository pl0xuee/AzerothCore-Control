using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace AzerothCoreControl.App.Behaviors;

/// <summary>
/// Attached behavior that keeps an <see cref="ItemsControl"/> (e.g. the console ListBox) scrolled to the
/// newest item as rows are appended — but only when the user is already near the bottom, so scrolling up
/// to read history isn't yanked back down.
/// </summary>
public static class AutoScrollBehavior
{
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

        if ((bool)e.NewValue)
        {
            if (list.Items is INotifyCollectionChanged incc)
                incc.CollectionChanged += (_, args) => OnCollectionChanged(list, args);
        }
    }

    private static void OnCollectionChanged(ListBox list, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action != NotifyCollectionChangedAction.Add || list.Items.Count == 0)
            return;

        var scrollViewer = FindScrollViewer(list);
        // Auto-follow only if the user is within a small threshold of the bottom.
        var atBottom = scrollViewer == null ||
                       scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 24;
        if (atBottom)
            list.Dispatcher.BeginInvoke(() => list.ScrollIntoView(list.Items[^1]));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
}
