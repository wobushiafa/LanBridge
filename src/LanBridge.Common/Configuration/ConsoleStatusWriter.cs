using System.Text.RegularExpressions;

namespace LanBridge.Common.Configuration;

public enum StatusKind
{
    Normal,
    P2P,
    Relay,
    Error,
    HolePunch
}

public sealed class ConsoleStatusWriter
{
    private readonly Regex _p2pRegex = new("P2P connection established", RegexOptions.IgnoreCase);
    private readonly Regex _relayRegex = new("relay", RegexOptions.IgnoreCase);
    private readonly Regex _errorRegex = new("error|failed|timeout", RegexOptions.IgnoreCase);
    private readonly Regex _holePunchRegex = new("State:|Hole punch", RegexOptions.IgnoreCase);

    public void WriteStatus(string status)
    {
        switch (ClassifyStatus(status))
        {
            case StatusKind.P2P:
                WriteModeBanner("P2P DIRECT", ConsoleColor.Green);
                WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Green);
                break;
            case StatusKind.Relay:
                WriteModeBanner("RELAY MODE", ConsoleColor.Yellow);
                WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Yellow);
                break;
            case StatusKind.Error:
                WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Red);
                break;
            case StatusKind.HolePunch:
                WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Cyan);
                break;
            default:
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status}");
                break;
        }
    }

    public StatusKind ClassifyStatus(string status)
    {
        if (_p2pRegex.IsMatch(status))
        {
            return StatusKind.P2P;
        }

        if (status.Contains("Relay connection established", StringComparison.OrdinalIgnoreCase))
        {
            return StatusKind.Relay;
        }

        if (_relayRegex.IsMatch(status))
        {
            return StatusKind.Relay;
        }

        if (_errorRegex.IsMatch(status))
        {
            return StatusKind.Error;
        }

        if (_holePunchRegex.IsMatch(status))
        {
            return StatusKind.HolePunch;
        }

        return StatusKind.Normal;
    }

    public void WriteModeBanner(string mode, ConsoleColor color)
    {
        WriteColored("", color);
        WriteColored("============================================================", color);
        WriteColored($"  TRANSPORT MODE: {mode}", color);
        WriteColored("============================================================", color);
    }

    public void WriteColored(string message, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }
}
