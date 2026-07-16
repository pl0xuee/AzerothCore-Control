using System.ComponentModel;
using System.Windows;

namespace AzerothCoreControl.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Closing the window hides it to the tray instead of exiting; Quit is via the tray menu.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private bool _reallyClosing;

    public void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }
}
