using System.Diagnostics;
using System.Text.Json;

namespace CodexBar;

internal sealed class AntigravityUsageProvider : IUsageProvider
{
    public const string ProviderId = "antigravity";
    private const string StatusFileEnvVar = "CODEXBAR_ANTIGRAVITY_STATUS_FILE";

    private readonly string statusFilePath;
    private readonly Func<bool> isClientRunning;
    private string? lastObservedAccountKey;
    private bool hasObservedAccount;

    public AntigravityUsageProvider()
        : this(ResolveStatusFilePath(), IsAntigravityProcessRunning)
    {
    }

    internal AntigravityUsageProvider(string statusFilePath, Func<bool> isClientRunning)
    {
        this.statusFilePath = statusFilePath;
        this.isClientRunning = isClientRunning;
    }

    public string Id => ProviderId;
    public string TabLabel => "ANTIGRAVITY";
    public string DisplayName => "Antigravity";

    public static string DefaultStatusFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBar",
        "antigravity-status.json");

    public static string ResolveStatusFilePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(StatusFileEnvVar);
        return string.IsNullOrWhiteSpace(overridePath) ? DefaultStatusFilePath : overridePath.Trim();
    }

    public bool CheckAccountChanged()
    {
        if (!TryReadAccountKey(out var currentKey)) return false;

        if (!hasObservedAccount)
        {
            lastObservedAccountKey = currentKey;
            hasObservedAccount = true;
            return false;
        }

        if (string.Equals(lastObservedAccountKey, currentKey, StringComparison.OrdinalIgnoreCase))
            return false;

        lastObservedAccountKey = currentKey;
        return true;
    }

    public Task<ProviderSnapshot> ReadUsageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        if (!isClientRunning())
        {
            return Task.FromResult(new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Standby,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: "Antigravity standby (not running)"));
        }

        if (!File.Exists(statusFilePath))
        {
            return Task.FromResult(new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Offline,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: "Waiting for agy statusLine bridge…"));
        }

        try
        {
            using var stream = new FileStream(
                statusFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var observedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(statusFilePath), TimeSpan.Zero);
            var snapshot = AntigravityUsageParser.Parse(document.RootElement, observedAt);

            if (snapshot.AccountKey is not null)
            {
                lastObservedAccountKey = snapshot.AccountKey;
                hasObservedAccount = true;
            }

            return Task.FromResult(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Task.FromResult(new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Offline,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: "Status bridge file invalid or unreadable"));
        }
    }

    private bool TryReadAccountKey(out string? accountKey)
    {
        accountKey = null;
        if (!isClientRunning() || !File.Exists(statusFilePath)) return false;

        try
        {
            using var stream = new FileStream(
                statusFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            var snapshot = AntigravityUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);
            if (string.IsNullOrWhiteSpace(snapshot.AccountKey)) return false;

            accountKey = snapshot.AccountKey;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAntigravityProcessRunning()
    {
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var name = process.ProcessName;
                    if (string.Equals(name, "agy", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "antigravity", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Process exited or access denied while inspecting name.
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public void Dispose()
    {
    }
}
