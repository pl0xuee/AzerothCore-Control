using System.IO;
using AzerothCoreControl.Core.Services;
using AzerothCoreControl.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

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
    [ObservableProperty] private string _detectedDatabases = "";
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
        DetectedDatabases = string.Join(", ", s.MySql.Databases);
    }

    // ---- Browse buttons -------------------------------------------------

    [RelayCommand]
    private void BrowseRunDir() => RunDirectory = PickFolder(RunDirectory) ?? RunDirectory;

    [RelayCommand]
    private void BrowseSourceDir() => SourceDirectory = PickFolder(SourceDirectory) ?? SourceDirectory;

    [RelayCommand]
    private void BrowseBuildDir() => BuildDirectory = PickFolder(BuildDirectory) ?? BuildDirectory;

    [RelayCommand]
    private void BrowseBackupDir() => BackupDirectory = PickFolder(BackupDirectory) ?? BackupDirectory;

    [RelayCommand]
    private void BrowseMysqlDump()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate mysqldump.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = SafeDir(MysqlDumpPath),
        };
        if (dialog.ShowDialog() == true)
            MysqlDumpPath = dialog.FileName;
    }

    private static string? PickFolder(string? current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder",
            InitialDirectory = SafeDir(current) ?? "",
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>Return an existing directory to seed a dialog, or null.</summary>
    private static string? SafeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path)) return path;
            var parent = Directory.GetParent(path)?.FullName;
            return parent != null && Directory.Exists(parent) ? parent : null;
        }
        catch { return null; }
    }

    [RelayCommand]
    private void AutoDetect()
    {
        var detected = PathDetector.Detect(RunDirectory ?? SourceDirectory);
        RunDirectory ??= detected.RunDirectory;
        SourceDirectory ??= detected.SourceDirectory;
        BuildDirectory ??= detected.BuildDirectory;

        var messages = new List<string>
        {
            detected.HasWorldServer ? "Found AzerothCore install." : "No worldserver.exe found — set paths manually.",
        };

        // MySQL Windows service.
        var services = _coordinator.MySql.DiscoverCandidateServices();
        if (services.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(MySqlServiceName))
                MySqlServiceName = services[0];
            messages.Add($"MySQL service: {MySqlServiceName}");
        }

        // Databases + connection details, read straight from AzerothCore's own .conf files.
        var db = AcoreConfigReader.Detect(RunDirectory);
        if (db.Found)
        {
            var mysql = _coordinator.Settings.MySql;
            if (!string.IsNullOrWhiteSpace(db.Host)) mysql.Host = db.Host!;
            if (db.Port is { } port) mysql.Port = port;
            if (!string.IsNullOrWhiteSpace(db.User)) mysql.Username = db.User!;
            if (db.Password != null) mysql.Password = db.Password;
            mysql.Databases = db.Databases;
            DetectedDatabases = string.Join(", ", db.Databases);
            messages.Add($"Databases: {DetectedDatabases}");
        }

        // Default the backup folder next to the server if not set.
        if (string.IsNullOrWhiteSpace(BackupDirectory) && !string.IsNullOrWhiteSpace(RunDirectory))
            BackupDirectory = Path.Combine(RunDirectory!, "backups");

        SaveResult = string.Join("   ·   ", messages);
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
