using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TheDojo.Core.Limits;

/// <summary>
/// Asks the Copilot CLI itself for the signed-in account's quota (<c>account.getQuota</c> over its headless
/// JSON-RPC mode), so the limits belong to the account the CLI uses (including a company account on GitHub
/// Enterprise Cloud) and The Dojo never handles a GitHub token.
/// </summary>
public static class CopilotCliQuota
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    /// <summary>The <c>account.getQuota</c> result as JSON, or null when the CLI isn't installed, isn't signed in or doesn't answer.</summary>
    public static async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (CommandResolver.Resolve("copilot") is not { } resolved)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Process? process = null;
        try
        {
            var isScript = resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            const string arguments = "--headless --no-auto-update --stdio";
            process = Process.Start(new ProcessStartInfo
            {
                FileName = isScript ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : resolved,
                Arguments = isScript ? $"/d /s /c \"\"{resolved}\" {arguments}\"" : arguments,
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return null;
            }

            _ = process.StandardError.ReadToEndAsync(CancellationToken.None); // never let a full stderr pipe block it

            const int id = 1;
            var request = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method = "account.getQuota", @params = new { } });
            var input = process.StandardInput.BaseStream;
            await input.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {request.Length}\r\n\r\n"), timeout.Token).ConfigureAwait(false);
            await input.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await input.FlushAsync(timeout.Token).ConfigureAwait(false);

            var output = process.StandardOutput.BaseStream;
            while (await ReadMessageAsync(output, timeout.Token).ConfigureAwait(false) is { } message)
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out _) || !root.TryGetProperty("id", out var answered) || answered.ValueKind != JsonValueKind.Number || answered.GetInt32() != id)
                {
                    continue; // a notification or someone else's answer
                }

                return root.TryGetProperty("result", out var result) ? result.GetRawText() : null;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or FormatException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // the CLI didn't answer in time
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // already gone
                }

                process.Dispose();
            }
        }
    }

    /// <summary>One <c>Content-Length</c> framed message, or null at the end of the stream.</summary>
    internal static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                if (length >= 0)
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return Encoding.UTF8.GetString(body);
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1)
        {
            if (one[0] == '\n')
            {
                return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            }

            bytes.Add(one[0]);
        }

        return null;
    }
}
