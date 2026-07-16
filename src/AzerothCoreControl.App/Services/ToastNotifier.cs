using System.Runtime.Versioning;
using AzerothCoreControl.Core.Services;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AzerothCoreControl.App.Services;

/// <summary>Shows native Windows toast notifications for coordinator events.</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public static class ToastNotifier
{
    public static void Show(string title, string message, NotificationSeverity severity)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch
        {
            // Toasts require a registered AppUserModelId / shortcut; never let a toast failure crash the app.
        }
    }
}
