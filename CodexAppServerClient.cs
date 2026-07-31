using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CodexBar;

internal sealed record UsageSnapshot(double UsedPercent, DateTimeOffset ResetsAt, DateTimeOffset CapturedAt);

internal sealed class CodexAppServerClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private Process? process;
    private StreamWriter? input;
    private StreamReader? output;
    private Task? stderrPump;
    private long nextRequestId;
    private bool disposed;

    public async Task<UsageSnapshot> ReadWeeklyAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        Exception? firstFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await EnsureStartedAsync();
                var response = await SendRequestAsync("account/rateLimits/read");
                return ParseWeekly(response);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or TimeoutException)
            {
                firstFailure ??= ex;
                StopProcess();
            }
        }

        throw new InvalidOperationException("Codex could not return live rate limits.", firstFailure);
    }

    private async Task EnsureStartedAsync()
    {
        if (process is { HasExited: false } && input is not null && output is not null) return;

        StopProcess();
        var executables = FindCodexExecutables();
        if (executables.Count == 0)
            throw new InvalidOperationException("A Codex installation with app-server support was not found.");

        Exception? firstFailure = null;
        foreach (var executable in executables)
        {
            try
            {
                await StartAsync(executable);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or InvalidOperationException or TimeoutException)
            {
                firstFailure ??= ex;
                StopProcess();
            }
        }

        throw new InvalidOperationException("No installed Codex executable could start app-server.", firstFailure);
    }

    private async Task StartAsync(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Codex app-server did not start.");
        input = process.StandardInput;
        input.AutoFlush = true;
        output = process.StandardOutput;
        stderrPump = DrainErrorsAsync(process.StandardError);

        var initializeId = Interlocked.Increment(ref nextRequestId);
        var initialize = JsonSerializer.Serialize(new
        {
            method = "initialize",
            id = initializeId,
            @params = new
            {
                clientInfo = new { name = "codexbar", title = "CodexBar", version = "1.0.0" }
            }
        });
        await input.WriteLineAsync(initialize);
        await ReadResponseAsync(initializeId);
        await input.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
    }

    private async Task<JsonElement> SendRequestAsync(string method)
    {
        var id = Interlocked.Increment(ref nextRequestId);
        await input!.WriteLineAsync(JsonSerializer.Serialize(new { method, id }));
        return await ReadResponseAsync(id);
    }

    private async Task<JsonElement> ReadResponseAsync(long id)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        while (true)
        {
            string? line;
            try { line = await output!.ReadLineAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("Codex rate-limit request timed out."); }
            if (line is null) throw new IOException("Codex app-server closed its output stream.");

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt64(out var value) || value != id)
                continue; // Account and app-server notifications can arrive between responses.

            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "Codex app-server returned an error."
                    : "Codex app-server returned an error.");

            return root.Clone();
        }
    }

    private static UsageSnapshot ParseWeekly(JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rateLimits", out var limits) ||
            limits.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Codex returned no ChatGPT rate limits. Sign in to Codex first.");

        if (limits.TryGetProperty("limitId", out var limitId) &&
            limitId.ValueKind == JsonValueKind.String &&
            !string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex returned a model-specific pool instead of the account limit.");

        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (!window.TryGetProperty("windowDurationMins", out var minutes) ||
                !minutes.TryGetInt32(out var duration) || duration != 7 * 24 * 60) continue;
            if (!window.TryGetProperty("usedPercent", out var used) || !used.TryGetDouble(out var percent) ||
                !window.TryGetProperty("resetsAt", out var reset) || !reset.TryGetInt64(out var unixReset)) continue;

            return new UsageSnapshot(Math.Clamp(percent, 0, 100),
                DateTimeOffset.FromUnixTimeSeconds(unixReset), DateTimeOffset.UtcNow);
        }

        throw new InvalidOperationException("Codex returned no weekly rate-limit window.");
    }

    private static List<string> FindCodexExecutables()
    {
        var candidates = new List<string>();
        AddExecutablesBelow(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin"));
        AddExecutablesBelow(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex"));

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var path = Path.Combine(directory.Trim('"'), "codex.exe");
                    if (File.Exists(path)) candidates.Add(path);
                }
                catch (ArgumentException) { }
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ToList();
    }

    private static void AddExecutablesBelow(List<string> candidates, string root)
    {
        if (!Directory.Exists(root)) return;
        try { candidates.AddRange(Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) Debug.WriteLine($"Codex app-server: {line}");
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void StopProcess()
    {
        input?.Dispose();
        output?.Dispose();
        input = null;
        output = null;

        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
            process = null;
        }
        stderrPump = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopProcess();
    }
}
