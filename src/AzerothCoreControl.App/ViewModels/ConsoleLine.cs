namespace AzerothCoreControl.App.ViewModels;

/// <summary>How a console line should be presented — drives its colour in the console list.</summary>
public enum ConsoleSeverity
{
    Info,
    Warning,
    Error,
    Command,
}

/// <summary>One rendered console row: when it arrived, what it said, and how loud it was.</summary>
public sealed record ConsoleLine(DateTime Timestamp, string Text, ConsoleSeverity Severity)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    /// <summary>Ctrl+C on selected console rows copies this, so keep it to what the user sees.</summary>
    public override string ToString() => $"{TimeText}  {Text}";

    /// <summary>
    /// Classifies a raw server line. AzerothCore tags its own lines ("ERROR:", "FATAL", "WARNING"), so a
    /// substring match on the first stretch of the line is enough — and cheap, which matters because startup
    /// pushes thousands of lines through here.
    /// </summary>
    public static ConsoleLine FromServer(string line)
    {
        var head = line.Length > 120 ? line[..120] : line;
        var severity = ConsoleSeverity.Info;
        if (head.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
            severity = ConsoleSeverity.Error;
        else if (head.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            severity = ConsoleSeverity.Warning;
        return new ConsoleLine(DateTime.Now, line, severity);
    }
}
