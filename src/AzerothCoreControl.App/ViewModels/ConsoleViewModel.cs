using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Hosts one independent console per server so world and auth output never interleave.</summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
    public ServerConsoleViewModel World { get; }
    public ServerConsoleViewModel Auth { get; }

    public ConsoleViewModel(ServerCoordinator coordinator)
    {
        World = new ServerConsoleViewModel(coordinator.World);
        Auth = new ServerConsoleViewModel(coordinator.Auth);
    }
}
