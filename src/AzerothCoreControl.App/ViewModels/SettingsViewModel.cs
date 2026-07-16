using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>Editable settings surface. Values are pushed back onto the coordinator's settings on Save.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private string? _runDirectory;
    [ObservableProperty] private string? _sourceDirectory;
    [ObservableProperty] private string? _buildDirectory;
    [ObservableProperty] private string? _mySqlServiceName;
    [ObservableProperty] private string? _mysqlDumpPath;
    [ObservableProperty] private string? _backupDirectory;
    [ObservableProperty] private string? _gitHubToken;
    [ObservableProperty] private string? _discordWebhookUrl;
    [ObservableProperty] private bool _autoRestart;
    [ObservableProperty] private bool _autoStartServers;
    [ObservableProperty] private bool _launchOnBoot;
    [ObservableProperty] private string _saveResult = "";

    public SettingsViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var s = _coordinator.Settings;
        RunDirectory = s.RunDirectory;
        SourceDirectory = s.SourceDirectory;
        BuildDirectory = s.BuildDirectory;
        MySqlServiceName = s.MySql.ServiceName;
        MysqlDumpPath = s.Backup.MysqlDumpPath;
        BackupDirectory = s.Backup.OutputDirectory;
        GitHubToken = s.GitHub.Token;
        DiscordWebhookUrl = s.Notifications.DiscordWebhookUrl;
        AutoRestart = s.Watchdog.AutoRestart;
        AutoStartServers = s.AutoStartServers;
        LaunchOnBoot = s.LaunchOnBoot;
    }

    [RelayCommand]
    private void AutoDetect()
    {
        var detected = PathDetector.Detect(RunDirectory ?? SourceDirectory);
        RunDirectory ??= detected.RunDirectory;
        SourceDirectory ??= detected.SourceDirectory;
        BuildDirectory ??= detected.BuildDirectory;
        SaveResult = detected.HasWorldServer
            ? "Found an AzerothCore install."
            : "Could not auto-detect — set the paths manually.";
    }

    [RelayCommand]
    private void Save()
    {
        var s = _coordinator.Settings;
        s.RunDirectory = Blank(RunDirectory);
        s.SourceDirectory = Blank(SourceDirectory);
        s.BuildDirectory = Blank(BuildDirectory);
        s.DeployDirectory = Blank(RunDirectory);
        s.MySql.ServiceName = Blank(MySqlServiceName);
        s.Backup.MysqlDumpPath = MysqlDumpPath ?? "mysqldump";
        s.Backup.OutputDirectory = Blank(BackupDirectory);
        s.GitHub.Token = Blank(GitHubToken);
        s.Notifications.DiscordWebhookUrl = Blank(DiscordWebhookUrl);
        s.Watchdog.AutoRestart = AutoRestart;
        s.AutoStartServers = AutoStartServers;
        s.LaunchOnBoot = LaunchOnBoot;

        _coordinator.SaveSettings();

        if (OperatingSystem.IsWindows())
            AutostartManager.SetEnabled(LaunchOnBoot);

        SaveResult = "Settings saved.";
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
