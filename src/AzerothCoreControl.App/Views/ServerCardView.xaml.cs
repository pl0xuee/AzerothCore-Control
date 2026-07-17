using System.Windows.Controls;

namespace AzerothCoreControl.App.Views;

/// <summary>One server's dashboard card. Bound to a <c>ServerStatusViewModel</c> via DataContext.</summary>
public partial class ServerCardView : UserControl
{
    public ServerCardView() => InitializeComponent();
}
