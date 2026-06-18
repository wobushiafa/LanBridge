namespace LanBridge.Common.Runtime;

public static class ConsoleStatusWriter
{
    public static void WriteHeader(string title)
    {
        Console.WriteLine($"=== {title} ===");
        Console.WriteLine();
    }

    public static void WriteConfiguration(IEnumerable<(string Label, string Value)> items)
    {
        Console.WriteLine("Configuration:");
        foreach (var item in items)
        {
            Console.WriteLine($"  {item.Label}: {item.Value}");
        }
        Console.WriteLine();
    }

    public static void WritePeerStatus(string status)
    {
        if (status.Contains("P2P connection established", StringComparison.OrdinalIgnoreCase))
        {
            WriteModeBanner("P2P DIRECT", ConsoleColor.Green);
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Green);
            return;
        }

        if (status.Contains("Relay connection established", StringComparison.OrdinalIgnoreCase))
        {
            WriteModeBanner("RELAY MODE", ConsoleColor.Yellow);
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Yellow);
            return;
        }

        if (status.Contains("relay", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Yellow);
            return;
        }

        if (status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Red);
            return;
        }

        if (status.StartsWith("State:", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Hole punch", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Cyan);
            return;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status}");
    }

    public static void WriteServerStatus(string source, string status, ConsoleColor color = ConsoleColor.Gray)
    {
        WriteColored($"[{DateTime.Now:HH:mm:ss}] [{source}] {status}", color);
    }

    public static void WriteColored(string message, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }

    private static void WriteModeBanner(string mode, ConsoleColor color)
    {
        WriteColored(string.Empty, color);
        WriteColored("============================================================", color);
        WriteColored($"  TRANSPORT MODE: {mode}", color);
        WriteColored("============================================================", color);
    }
}
