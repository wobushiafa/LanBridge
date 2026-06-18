namespace LanBridge.Common.Runtime;

public static class CommandLineArguments
{
    public static string? FindOptionValue(string[] args, params string[] optionNames)
    {
        if (args.Length == 0 || optionNames.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (optionNames.Contains(args[i], StringComparer.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public static bool HasOption(string[] args, params string[] optionNames)
    {
        if (args.Length == 0 || optionNames.Length == 0)
        {
            return false;
        }

        return args.Any(arg => optionNames.Contains(arg, StringComparer.Ordinal));
    }
}
