using System.Text.Json;

namespace TheDojo.Core.Ingest;

/// <summary>Works out what a tool call ran on, so behaviour can be grouped (files re-read, programs run, sites fetched).</summary>
internal static class ToolTargets
{
    private static readonly string[] PathArguments = ["file_path", "path", "notebook_path", "filePath"];
    private static readonly string[] CommandArguments = ["command", "cmd"];

    public static string? Of(string toolName, JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } args)
        {
            return null;
        }

        var name = toolName.ToLowerInvariant();
        if (name is "agent" or "task")
        {
            return Json.Str(args, "subagent_type") ?? Json.Str(args, "agent_type") ?? "general-purpose";
        }

        if (name == "skill")
        {
            return Json.Str(args, "skill") ?? Json.Str(args, "name");
        }

        if (name is "webfetch" or "web_fetch" or "fetch")
        {
            return Host(Json.Str(args, "url"));
        }

        foreach (var key in CommandArguments)
        {
            if (Json.Str(args, key) is { Length: > 0 } command)
            {
                return Program(command);
            }
        }

        foreach (var key in PathArguments)
        {
            if (Json.Str(args, key) is { Length: > 0 } path)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The program a shell command runs, skipping <c>cd</c> and environment prefixes: <c>cd x &amp;&amp; dotnet test</c> is <c>dotnet</c>.</summary>
    public static string? Program(string command)
    {
        foreach (var segment in command.Split(["&&", "||", ";", "|", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A quoted program path may hold spaces: "C:\Program Files\Git\bin\git.exe" status
            var quote = segment.Length > 1 && segment[0] is '"' or '\'' ? segment.IndexOf(segment[0], 1) : -1;
            var words = quote > 0
                ? [segment[1..quote], .. segment[(quote + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)]
                : segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var i = 0;
            while (i < words.Length && (words[i].Contains('=') && !words[i].StartsWith('-') || words[i] is "sudo" or "&" or "time"))
            {
                i++;
            }

            if (i >= words.Length)
            {
                continue;
            }

            var program = words[i].Trim('"', '\'', '(', ')');
            if (program is "cd" or "pushd" or "set-location" or "Set-Location" or "")
            {
                continue;
            }

            var slash = program.LastIndexOfAny(['/', '\\']);
            if (slash >= 0)
            {
                program = program[(slash + 1)..];
            }

            if (program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                program = program[..^4];
            }

            return program.Length == 0 ? null : program;
        }

        return null;
    }

    private static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>Claude Code names MCP tools <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
    public static string? McpServer(string toolName)
    {
        if (!toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return null;
        }

        var end = toolName.IndexOf("__", 5, StringComparison.Ordinal);
        return end > 5 ? toolName[5..end] : toolName[5..];
    }
}
