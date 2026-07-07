namespace LanBridge.Common.Configuration;

public static class CommandLineParser
{
    public static string? FindOptionValue(string[] args, string longName, string shortName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == longName || args[i] == shortName)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
