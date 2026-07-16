using System.Runtime.Versioning;
using System.ServiceProcess;
using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public enum MySqlState { Unknown, Running, Stopped, NotConfigured, NotFound }

/// <summary>
/// Monitors and controls the MySQL/MariaDB Windows service the AzerothCore servers depend on.
/// All Windows-service calls are guarded by <see cref="OperatingSystem.IsWindows"/> so the Core
/// library still loads on the Linux dev box (where it simply reports <see cref="MySqlState.Unknown"/>).
/// </summary>
public sealed class MySqlMonitor
{
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public MySqlMonitor(Func<AppSettings> settings, ILogger<MySqlMonitor>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<MySqlMonitor>.Instance;
    }

    public MySqlState GetState()
    {
        var name = _settings().MySql.ServiceName;
        if (string.IsNullOrWhiteSpace(name))
            return MySqlState.NotConfigured;
        if (!OperatingSystem.IsWindows())
            return MySqlState.Unknown;
        return GetStateWindows(name);
    }

    [SupportedOSPlatform("windows")]
    private MySqlState GetStateWindows(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status switch
            {
                ServiceControllerStatus.Running or ServiceControllerStatus.StartPending => MySqlState.Running,
                _ => MySqlState.Stopped,
            };
        }
        catch (InvalidOperationException)
        {
            return MySqlState.NotFound;
        }
    }

    /// <summary>Start the MySQL service and wait until it reports Running (or the timeout elapses).</summary>
    public async Task<bool> EnsureRunningAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var name = _settings().MySql.ServiceName;
        if (string.IsNullOrWhiteSpace(name) || !OperatingSystem.IsWindows())
            return GetState() == MySqlState.Running;
        return await Task.Run(() => StartAndWaitWindows(name, timeout), cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private bool StartAndWaitWindows(string name, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(name);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running)
                return true;
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
                sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            _log.LogWarning(ex, "Could not start MySQL service {Name}", name);
            return false;
        }
    }

    /// <summary>Enumerate installed services whose name looks like MySQL/MariaDB, to help the setup wizard.</summary>
    public IReadOnlyList<string> DiscoverCandidateServices()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();
        return DiscoverWindows();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> DiscoverWindows()
    {
        try
        {
            return ServiceController.GetServices()
                .Where(s => s.ServiceName.Contains("mysql", StringComparison.OrdinalIgnoreCase)
                         || s.ServiceName.Contains("maria", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.ServiceName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
