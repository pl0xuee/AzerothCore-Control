using System.Collections.ObjectModel;
using System.IO;
using AzerothCoreControl.Core.Models;
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
    [ObservableProperty] private string? _cMakePath;
    [ObservableProperty] private string? _cMakeGuiPath;
    [ObservableProperty] private bool _reviewCMakeBeforeBuild;
    [ObservableProperty] private string? _mySqlServiceName;
    [ObservableProperty] private string? _mysqlDumpPath;
    [ObservableProperty] private string? _backupDirectory;

    /// <summary>Existed in settings but had no UI, so it could only be changed by editing settings.json.</summary>
    [ObservableProperty] private bool _includeConfigsInBackup;
    [ObservableProperty] private string? _gitHubToken;
    [ObservableProperty] private string? _discordWebhookUrl;
    [ObservableProperty] private bool _autoRestart;
    [ObservableProperty] private bool _autoStartServers;
    [ObservableProperty] private bool _launchOnBoot;
    [ObservableProperty] private string _detectedDatabases = "";
    [ObservableProperty] private string _saveResult = "";
    [ObservableProperty] private ModuleRepoOverride? _selectedOverride;

    /// <summary>
    /// Modules pinned to a specific repo. The catalogue guesses by folder name, which lands on the popular
    /// upstream — wrong for anyone deliberately running a fork.
    /// </summary>
    public ObservableCollection<ModuleRepoOverride> ModuleRepoOverrides { get; } = new();

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
        CMakePath = s.Build.CMakePath;
        CMakeGuiPath = s.Build.CMakeGuiPath;
        ReviewCMakeBeforeBuild = s.Build.ReviewCMakeBeforeBuild;
        MySqlServiceName = s.MySql.ServiceName;
        MysqlDumpPath = s.Backup.MysqlDumpPath;
        BackupDirectory = s.Backup.OutputDirectory;
        IncludeConfigsInBackup = s.Backup.IncludeConfigs;
        GitHubToken = s.GitHub.Token;
        DiscordWebhookUrl = s.Notifications.DiscordWebhookUrl;
        AutoRestart = s.Watchdog.AutoRestart;
        AutoStartServers = s.AutoStartServers;
        LaunchOnBoot = s.LaunchOnBoot;
        DetectedDatabases = string.Join(", ", s.MySql.Databases);

        // Copy, so cancelling out of Settings without saving doesn't mutate live settings.
        ModuleRepoOverrides.Clear();
        foreach (var o in s.ModuleRepoOverrides)
            ModuleRepoOverrides.Add(new ModuleRepoOverride { Module = o.Module, Repository = o.Repository });

        RefreshLaunchOnBootFromSystem();
    }

    /// <summary>
    /// Correct <see cref="LaunchOnBoot"/> from the registered logon task. settings.json only records what
    /// we last asked for; the task is the truth, and the two drift as soon as anything outside the app
    /// touches it. A checkbox reporting its own saved value is precisely why the old (permanently broken)
    /// Run-key autostart went unnoticed. Runs off-thread: it shells out to schtasks, and this view model
    /// is constructed during startup.
    /// </summary>
    private void RefreshLaunchOnBootFromSystem()
    {
        if (!OperatingSystem.IsWindows())
            return;

        _ = Task.Run(() =>
        {
            bool registered;
            try
            {
                registered = AutostartManager.IsEnabled();
            }
            catch
            {
                return; // Couldn't ask — leave the saved value alone rather than guess.
            }

            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => LaunchOnBoot = registered);
            }
            catch { /* shutting down */ }
        });
    }

    [RelayCommand]
    private void AddModuleRepoOverride()
        => ModuleRepoOverrides.Add(new ModuleRepoOverride { Module = "", Repository = "" });

    [RelayCommand]
    private void RemoveModuleRepoOverride()
    {
        if (SelectedOverride != null)
            ModuleRepoOverrides.Remove(SelectedOverride);
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
        var picked = PickExecutable("Locate mysqldump.exe", MysqlDumpPath);
        if (picked != null)
            MysqlDumpPath = picked;
    }

    [RelayCommand]
    private void BrowseCMake()
    {
        var picked = PickExecutable("Locate cmake.exe", CMakePath);
        if (picked != null)
            CMakePath = picked;
    }

    [RelayCommand]
    private void BrowseCMakeGui()
    {
        var picked = PickExecutable("Locate cmake-gui.exe", CMakeGuiPath ?? CMakePath);
        if (picked != null)
            CMakeGuiPath = picked;
    }

    private static string? PickExecutable(string title, string? current)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = SafeDir(current),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
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
        // Blank(), not ??=: a TextBox the user has cleared holds "" rather than null, and "" is not null — so
        // ??= silently refused to fill in exactly the empty box the user pressed this button to fill.
        var detected = PathDetector.Detect(Blank(RunDirectory) ?? Blank(SourceDirectory));
        RunDirectory = Blank(RunDirectory) ?? detected.RunDirectory;
        SourceDirectory = Blank(SourceDirectory) ?? detected.SourceDirectory;
        BuildDirectory = Blank(BuildDirectory) ?? detected.BuildDirectory;

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
        else
        {
            messages.Add("No worldserver.conf/authserver.conf found — using default database names.");
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
        s.Build.CMakePath = Blank(CMakePath) ?? "cmake";
        s.Build.CMakeGuiPath = Blank(CMakeGuiPath);
        s.Build.ReviewCMakeBeforeBuild = ReviewCMakeBeforeBuild;
        s.MySql.ServiceName = Blank(MySqlServiceName);
        s.Backup.MysqlDumpPath = MysqlDumpPath ?? "mysqldump";
        s.Backup.OutputDirectory = Blank(BackupDirectory);
        s.Backup.IncludeConfigs = IncludeConfigsInBackup;
        s.GitHub.Token = Blank(GitHubToken);
        s.Notifications.DiscordWebhookUrl = Blank(DiscordWebhookUrl);
        s.Watchdog.AutoRestart = AutoRestart;
        s.AutoStartServers = AutoStartServers;
        s.LaunchOnBoot = LaunchOnBoot;

        // Drop half-filled rows rather than persisting entries that can never match anything.
        s.ModuleRepoOverrides = ModuleRepoOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.Module) && !string.IsNullOrWhiteSpace(o.Repository))
            .Select(o => new ModuleRepoOverride { Module = o.Module.Trim(), Repository = o.Repository.Trim() })
            .ToList();

        _coordinator.SaveSettings();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                AutostartManager.SetEnabled(LaunchOnBoot);
            }
            catch (Exception ex)
            {
                // Registering the logon task can genuinely fail (group policy, a locked-down machine).
                // Reporting "Settings saved." over the top of that is how the previous autostart bug
                // managed to stay invisible for 23 releases.
                SaveResult = "Settings saved, but launch-on-boot could not be changed: " + ex.Message;
                return;
            }
        }

        SaveResult = "Settings saved.";
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
