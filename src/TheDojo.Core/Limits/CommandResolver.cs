namespace TheDojo.Core.Limits;

/// <summary>Finds a command on the PATH the way a shell would (with PATHEXT on Windows).</summary>
public static class CommandResolver
{
    public static string? Resolve(string command, string? path = null, string? pathExt = null)
    {
        var folders = (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var folder in folders)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim('"'), command + ext.ToLowerInvariant());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
        }

        return null;
    }
}
