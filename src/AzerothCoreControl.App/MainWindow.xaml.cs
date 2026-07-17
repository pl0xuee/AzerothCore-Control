using System.ComponentModel;
using System.Windows;
using AzerothCoreControl.App.Services;

namespace AzerothCoreControl.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The title bar belongs to Windows, so the theme can't reach it — ask DWM for a dark one. This is the
    /// earliest point the HWND exists, and doing it before the window is shown avoids a flash of light chrome.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
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
