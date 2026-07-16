using System.Collections.ObjectModel;
using System.Windows.Threading;
using AzerothCoreControl.Core.Models;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>The worldserver console: live output plus a command input piped to stdin.</summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
    private const int MaxLines = 5000;
    private readonly ServerCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty] private string _commandInput = "";

    public ObservableCollection<string> Output { get; } = new();

    public ConsoleViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _coordinator.World.OutputLine += (kind, line) => Append(kind, line);
        _coordinator.Auth.OutputLine += (kind, line) => Append(kind, line);
    }

    private void Append(ServerKind kind, string line)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var prefix = kind == ServerKind.Auth ? "[auth] " : "";
            Output.Add(prefix + line);
            while (Output.Count > MaxLines)
                Output.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void SendCommand()
    {
        var cmd = CommandInput.Trim();
        if (cmd.Length == 0)
            return;
        _coordinator.World.SendConsole(cmd);
        Output.Add("> " + cmd);
        CommandInput = "";
    }

    [RelayCommand]
    private void Clear() => Output.Clear();
}
