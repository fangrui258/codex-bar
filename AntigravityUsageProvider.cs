using System.Diagnostics;
using System.Text.Json;

namespace CodexBar;

internal sealed class AntigravityUsageProvider : IUsageProvider
{
    public const string ProviderId = "antigravity";
    private const string StatusFileEnvVar = "CODEXBAR_ANTIGRAVITY_STATUS_FILE";
    private static readonly TimeSpan StaleSnapshotGracePeriod = TimeSpan.FromMinutes(2);

    private readonly string statusFilePath;
    private readonly Func<bool> isAgyRunning;
    private readonly Func<bool> isDesktopRunning;
    private readonly Func<DateTimeOffset> utcNowProvider;
    private string? lastObservedAccountKey;
    private bool hasObservedAccount;

    public AntigravityUsageProvider()
        : this(ResolveStatusFilePath(), IsAgyCliRunning, IsAntigravityDesktopRunning, () => DateTimeOffset.UtcNow)
    {
    }

    internal AntigravityUsageProvider(
        string statusFilePath,
        Func<bool> isAgyRunning,
        Func<bool>? isDesktopRunning = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        this.statusFilePath = statusFilePath;
        this.isAgyRunning = isAgyRunning;
        this.isDesktopRunning = isDesktopRunning ?? (() => false);
        this.utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
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
        var now = utcNowProvider();

        if (!isAgyRunning())
        {
            var standbyMessage = isDesktopRunning()
                ? "Desktop open · run agy CLI to sync"
                : "Antigravity standby (not running)";

            return Task.FromResult(new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Standby,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: standbyMessage));
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
                StatusMessage: "Waiting for agy statusLine update…"));
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

            if (HasExpiredReset(snapshot, now) && now - observedAt > StaleSnapshotGracePeriod)
            {
                return Task.FromResult(new ProviderSnapshot(
                    ProviderId,
                    TabLabel,
                    DisplayName,
                    ProviderStatus.Standby,
                    Array.Empty<QuotaLane>(),
                    now,
                    StatusMessage: "Refreshing stale agy snapshot…",
                    AccountKey: snapshot.AccountKey));
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

    internal static bool HasExpiredReset(ProviderSnapshot snapshot, DateTimeOffset now) =>
        snapshot.Lanes.Any(lane => lane.ResetsAt is { } resetsAt && resetsAt <= now);

    private bool TryReadAccountKey(out string? accountKey)
    {
        accountKey = null;
        if (!isAgyRunning() || !File.Exists(statusFilePath)) return false;

        try
        {
            using var stream = new FileStream(
                statusFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            var snapshot = AntigravityUsageParser.Parse(document.RootElement, utcNowProvider());
            if (string.IsNullOrWhiteSpace(snapshot.AccountKey)) return false;

            accountKey = snapshot.AccountKey;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAgyCliRunning() => IsProcessRunning("agy");

    public static bool IsAntigravityDesktopRunning() => IsProcessRunning("antigravity");

    private static bool IsProcessRunning(string expectedProcessName)
    {
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
                        return true;
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
