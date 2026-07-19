using System.Windows;
using System.Windows.Controls;

namespace AzerothCoreControl.App.Views;

/// <summary>
/// Compiler output from a failed build. Bound to a <c>BuildReportViewModel</c> via DataContext — the same
/// panel serves one module's row and the "Update all" batch.
/// </summary>
public partial class BuildReportView : UserControl
{
    /// <summary>
    /// What to show before any build has failed. A property rather than fixed text because the two uses need
    /// to say different things: one is about a module, the other about the whole batch.
    /// </summary>
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(BuildReportView), new PropertyMetadata(""));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public BuildReportView() => InitializeComponent();
}
