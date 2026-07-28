using System.Xml.Linq;
using AzerothCoreControl.Core.Services;
using Xunit;

namespace AzerothCoreControl.Core.Tests;

public class AutostartTaskTests
{
    private const string Exe = @"C:\Users\tester\Desktop\AzerothCoreControl.exe";
    private const string User = @"DESKTOP-ABC\tester";

    private static XElement Build(string exe = Exe, string user = User, string? workDir = null)
        => XElement.Parse(AutostartTaskDefinition.BuildXml(exe, user, workDir));

    /// <summary>Task Scheduler's XML lives in a default namespace, so every lookup needs it.</summary>
    private static XName N(string name) => XName.Get(name, "http://schemas.microsoft.com/windows/2004/02/mit/task");

    private static string? Value(XElement task, string name) => task.Descendants(N(name)).FirstOrDefault()?.Value;

    [Fact]
    public void RunsWithHighestPrivileges()
    {
        // The entire point of using a scheduled task over the Run key: the app manifest requests
        // requireAdministrator, and only Task Scheduler can grant that at logon without a UAC prompt.
        Assert.Equal("HighestAvailable", Value(Build(), "RunLevel"));
    }

    [Fact]
    public void RunsInteractivelySoTheTrayIconAndWindowAppear()
    {
        // S4U/Password logon types run without a desktop — the app would start with no visible UI at all.
        Assert.Equal("InteractiveToken", Value(Build(), "LogonType"));
    }

    [Fact]
    public void TriggersOnLogonForTheGivenUser()
    {
        var task = Build();
        var trigger = Assert.Single(task.Descendants(N("LogonTrigger")));
        Assert.Equal("true", trigger.Element(N("Enabled"))?.Value);
        Assert.Equal(User, trigger.Element(N("UserId"))?.Value);
    }

    [Fact]
    public void LaunchesTheExeMinimisedToTray()
    {
        var task = Build();
        Assert.Equal(Exe, Value(task, "Command"));
        Assert.Equal("--minimized", Value(task, "Arguments"));
    }

    [Fact]
    public void DefaultsTheWorkingDirectoryToTheExeFolder()
        => Assert.Equal(@"C:\Users\tester\Desktop", Value(Build(), "WorkingDirectory"));

    [Fact]
    public void UsesAnExplicitWorkingDirectoryWhenGiven()
        => Assert.Equal(@"D:\acore", Value(Build(workDir: @"D:\acore"), "WorkingDirectory"));

    [Fact]
    public void KeepsTheTrailingSlashForAnExeSittingAtADriveRoot()
        => Assert.Equal(@"C:\", Value(Build(exe: @"C:\AzerothCoreControl.exe"), "WorkingDirectory"));

    [Fact]
    public void StartsOnBatteryPower()
    {
        // Both of these default to true in Task Scheduler, which on a laptop means "never starts while
        // unplugged" and "gets killed the moment you unplug" — silent no-shows that look exactly like
        // the Run-key bug this replaced.
        var task = Build();
        Assert.Equal("false", Value(task, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(task, "StopIfGoingOnBatteries"));
    }

    [Fact]
    public void HasNoExecutionTimeLimit()
    {
        // Defaults to PT72H — Task Scheduler would terminate the supervisor (and its servers) after 3 days.
        Assert.Equal("PT0S", Value(Build(), "ExecutionTimeLimit"));
    }

    [Fact]
    public void DelaysSlightlyAfterLogon()
    {
        // The notification area is not reliably ready the instant a logon trigger fires; a tray-only
        // launch that registers too early can end up with no icon at all.
        var delay = Build().Descendants(N("LogonTrigger")).Single().Element(N("Delay"))?.Value;
        Assert.Equal("PT10S", delay);
    }

    [Fact]
    public void DoesNotStackInstances()
        => Assert.Equal("IgnoreNew", Value(Build(), "MultipleInstancesPolicy"));

    [Fact]
    public void EscapesXmlSpecialCharactersInPathsAndUserNames()
    {
        // A Windows account genuinely can be named "Bell & Co", and an unescaped & is malformed XML that
        // schtasks rejects outright.
        var task = Build(exe: @"C:\Users\Bell & Co\Desktop\AzerothCoreControl.exe", user: @"PC\Bell & Co");
        Assert.Equal(@"C:\Users\Bell & Co\Desktop\AzerothCoreControl.exe", Value(task, "Command"));
        Assert.Equal(@"PC\Bell & Co", task.Descendants(N("LogonTrigger")).Single().Element(N("UserId"))?.Value);
    }

    [Fact]
    public void DeclaresUtf16BecauseThatIsWhatSchtasksAccepts()
    {
        // schtasks /Create /XML rejects UTF-8 files containing non-ASCII, so the file is written as
        // UTF-16 and the declaration has to agree with it.
        Assert.Contains("encoding=\"UTF-16\"", AutostartTaskDefinition.BuildXml(Exe, User, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsAMissingExePath(string? exe)
        => Assert.Throws<ArgumentException>(() => AutostartTaskDefinition.BuildXml(exe!, User, null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsAMissingUserId(string? user)
        => Assert.Throws<ArgumentException>(() => AutostartTaskDefinition.BuildXml(Exe, user!, null));
}
