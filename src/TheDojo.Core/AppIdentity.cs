namespace TheDojo.Core;

/// <summary>The product name and the folders The Dojo keeps its caches and history in.</summary>
public static class AppIdentity
{
    public const string ProductName = "The Dojo";
    public const string FileName = "TheDojo";

    /// <summary>Caches and limit history. <c>DOJO_HOME</c> moves it (the tests use this).</summary>
    public static string LocalFolder =>
        Environment.GetEnvironmentVariable("DOJO_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FileName);

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Claude Code's config folder (<c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>).</summary>
    public static string ClaudeHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(UserProfile, ".claude");

    /// <summary>Copilot CLI's home (<c>COPILOT_HOME</c> or <c>~/.copilot</c>).</summary>
    public static string CopilotHome =>
        Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } dir ? dir : Path.Combine(UserProfile, ".copilot");
}
