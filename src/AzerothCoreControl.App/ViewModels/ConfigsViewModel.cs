using System.Collections.ObjectModel;
using AzerothCoreControl.App.Services;
using AzerothCoreControl.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothCoreControl.App.ViewModels;

/// <summary>
/// The Configs tab: pick a .conf, edit it, save it. Editing these by hand normally means alt-tabbing to a
/// text editor and remembering which of several same-named files you wanted.
/// </summary>
public sealed partial class ConfigsViewModel : ObservableObject
{
    private readonly ServerCoordinator _coordinator;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The editor's text. Compared against <see cref="_savedText"/> to know if it's dirty.</summary>
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [ObservableProperty] private string _text = "";

    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [ObservableProperty] private ConfigFileInfo? _selectedFile;

    private string _savedText = "";

    public ObservableCollection<ConfigFileInfo> Files { get; } = new();

    public bool HasSelection => SelectedFile != null;

    /// <summary>Unsaved changes — drives the Save button and the warning when switching files.</summary>
    public bool IsDirty => SelectedFile != null && !string.Equals(Text, _savedText, StringComparison.Ordinal);

    public ConfigsViewModel(ServerCoordinator coordinator)
    {
        _coordinator = coordinator;
        Refresh();
    }

    /// <summary>Re-scan for .conf files. Also called when the run directory changes in Settings.</summary>
    [RelayCommand]
    public void Refresh()
    {
        var previous = SelectedFile?.Path;
        Files.Clear();
        foreach (var file in _coordinator.ConfigEditor.ListFiles())
            Files.Add(file);

        if (Files.Count == 0)
        {
            SelectedFile = null;
            Text = "";
            _savedText = "";
            SetStatus("No .conf files found. Check the Run directory in Settings.", isError: false);
            return;
        }

        // Keep the user on the file they were editing across a refresh, if it's still there.
        SelectedFile = Files.FirstOrDefault(f => f.Path == previous) ?? Files[0];
    }

    partial void OnSelectedFileChanged(ConfigFileInfo? value)
    {
        if (value == null)
        {
            Text = "";
            _savedText = "";
            return;
        }

        var (ok, text, message) = _coordinator.ConfigEditor.Load(value.Path);
        if (!ok)
        {
            Text = "";
            _savedText = "";
            SetStatus(message, isError: true);
            return;
        }

        // Set _savedText first: assigning Text raises IsDirty, which must not momentarily read as dirty.
        _savedText = text;
        Text = text;
        SetStatus(value.Path, isError: false);
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedFile is not { } file)
            return;

        var result = _coordinator.ConfigEditor.Save(file.Path, Text);
        SetStatus(result.Message, isError: !result.Success);
        if (!result.Success)
            return;

        _savedText = Text;
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>Throw away edits and re-read the file from disk.</summary>
    [RelayCommand]
    private void Revert()
    {
        if (SelectedFile is not { } file)
            return;
        var (ok, text, message) = _coordinator.ConfigEditor.Load(file.Path);
        if (!ok)
        {
            SetStatus(message, isError: true);
            return;
        }
        _savedText = text;
        Text = text;
        SetStatus("Reverted to the file on disk.", isError: false);
    }

    private void SetStatus(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }
}
