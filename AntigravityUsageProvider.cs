using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexBar;

internal sealed class AntigravityUsageProvider : IUsageProvider
{
    public const string ProviderId = "antigravity";
    private const string StatusFileEnvVar = "CODEXBAR_ANTIGRAVITY_STATUS_FILE";
    private static readonly TimeSpan StaleSnapshotGracePeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultCliPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CliCommandTimeout = TimeSpan.FromSeconds(20);

    private readonly string statusFilePath;
    private readonly Func<bool> isExternalAgyRunning;
    private readonly Func<bool> isDesktopRunning;
    private readonly Func<DateTimeOffset> utcNowProvider;
    private readonly Func<CancellationToken, Task<ProviderSnapshot>>? cliUsageFetcher;
    private readonly TimeSpan cliPollInterval;

    private string? lastObservedAccountKey;
    private bool hasObservedAccount;
    private ProviderSnapshot? cachedCliSnapshot;
    private DateTimeOffset? lastCliFetchAt;
    private int activeCliPollProcessId;
    private bool disposed;

    public AntigravityUsageProvider()
    {
        statusFilePath = ResolveStatusFilePath();
        isExternalAgyRunning = () => IsExternalAgyCliRunning(Volatile.Read(ref activeCliPollProcessId));
        isDesktopRunning = IsAntigravityDesktopRunning;
        utcNowProvider = () => DateTimeOffset.UtcNow;
        cliPollInterval = DefaultCliPollInterval;
        cliUsageFetcher = FetchUsageViaCliAsync;
    }

    internal AntigravityUsageProvider(
        string statusFilePath,
        Func<bool> isExternalAgyRunning,
        Func<bool>? isDesktopRunning = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        Func<CancellationToken, Task<ProviderSnapshot>>? cliUsageFetcher = null,
        TimeSpan? cliPollInterval = null)
    {
        this.statusFilePath = statusFilePath;
        this.isExternalAgyRunning = isExternalAgyRunning;
        this.isDesktopRunning = isDesktopRunning ?? (() => false);
        this.utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        this.cliUsageFetcher = cliUsageFetcher;
        this.cliPollInterval = cliPollInterval ?? DefaultCliPollInterval;
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

    public async Task<ProviderSnapshot> ReadUsageAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var now = utcNowProvider();

        var agyRunning = isExternalAgyRunning();
        var desktopRunning = isDesktopRunning();
        if (!agyRunning && !desktopRunning)
        {
            cachedCliSnapshot = null;
            lastCliFetchAt = null;
            return new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Standby,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: "Antigravity standby (not running)");
        }

        if (cliUsageFetcher is not null)
        {
            if (!forceRefresh &&
                cachedCliSnapshot is not null &&
                lastCliFetchAt.HasValue &&
                now - lastCliFetchAt.Value < cliPollInterval &&
                !HasExpiredReset(cachedCliSnapshot, now))
            {
                return cachedCliSnapshot;
            }

            try
            {
                var cliSnapshot = await cliUsageFetcher(cancellationToken);
                cachedCliSnapshot = cliSnapshot;
                lastCliFetchAt = utcNowProvider();
                return cliSnapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Antigravity CLI usage check failed: {ex.Message}");
                if (agyRunning && TryReadStatusFile(now, out var fallbackSnapshot) &&
                    fallbackSnapshot.Status == ProviderStatus.Online)
                {
                    return fallbackSnapshot;
                }

                return new ProviderSnapshot(
                    ProviderId,
                    TabLabel,
                    DisplayName,
                    ProviderStatus.Offline,
                    Array.Empty<QuotaLane>(),
                    now,
                    StatusMessage: ex.Message);
            }
        }

        if (!agyRunning)
        {
            return new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Standby,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: "Desktop open · run agy CLI to sync");
        }

        return TryReadStatusFile(now, out var fileSnapshot)
            ? fileSnapshot
            : new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Offline,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: "Waiting for agy statusLine update…");
    }

    private async Task<ProviderSnapshot> FetchUsageViaCliAsync(CancellationToken cancellationToken)
    {
        var executables = FindAgyExecutables();
        if (executables.Count == 0)
            throw new InvalidOperationException("Install agy CLI to read desktop quota");

        Exception? firstFailure = null;
        foreach (var executable in executables)
        {
            try
            {
                return await RunCliUsageCommandAsync(executable, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        throw firstFailure ?? new InvalidOperationException("agy CLI did not return usage data.");
    }

    private async Task<ProviderSnapshot> RunCliUsageCommandAsync(string executable, CancellationToken cancellationToken)
    {
        var workingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBar");
        Directory.CreateDirectory(workingDir);

        var startedPid = 0;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CliCommandTimeout);

            var result = await HiddenDesktopProcessRunner.RunAsync(
                executable,
                new[] { "-p", "/usage", "--output-format", "json", "--print-timeout", "15s" },
                workingDir,
                pid =>
                {
                    startedPid = pid;
                    Interlocked.Exchange(ref activeCliPollProcessId, pid);
                },
                timeoutCts.Token);

            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"exit code {result.ExitCode}"
                    : result.StandardError.Trim();
                if (detail.Contains("not logged", StringComparison.OrdinalIgnoreCase) ||
                    detail.Contains("login", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Run agy once to sign in");
                throw new InvalidOperationException("agy CLI quota request failed");
            }

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                throw new InvalidOperationException("agy CLI returned no output");

            using var doc = JsonDocument.Parse(result.StandardOutput);
            return AntigravityUsageParser.Parse(doc.RootElement, utcNowProvider());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("agy CLI quota request timed out");
        }
        finally
        {
            if (startedPid > 0)
                Interlocked.CompareExchange(ref activeCliPollProcessId, 0, startedPid);
        }
    }

    internal static List<string> FindAgyExecutables()
    {
        var candidates = new List<string>();
        var defaultBinary = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "agy",
            "bin",
            "agy.exe");
        if (File.Exists(defaultBinary))
            candidates.Add(defaultBinary);

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var path = Path.Combine(directory.Trim('"'), "agy.exe");
                    if (File.Exists(path))
                        candidates.Add(path);
                }
                catch (ArgumentException) { }
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    private bool TryReadStatusFile(DateTimeOffset now, out ProviderSnapshot snapshot)
    {
        if (!File.Exists(statusFilePath))
        {
            snapshot = null!;
            return false;
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
            snapshot = AntigravityUsageParser.Parse(document.RootElement, observedAt);

            if (snapshot.AccountKey is not null)
            {
                lastObservedAccountKey = snapshot.AccountKey;
                hasObservedAccount = true;
            }

            if (HasExpiredReset(snapshot, now) && now - observedAt > StaleSnapshotGracePeriod)
            {
                snapshot = new ProviderSnapshot(
                    ProviderId,
                    TabLabel,
                    DisplayName,
                    ProviderStatus.Standby,
                    Array.Empty<QuotaLane>(),
                    now,
                    StatusMessage: "Refreshing stale agy snapshot…",
                    AccountKey: snapshot.AccountKey);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            snapshot = new ProviderSnapshot(
                ProviderId,
                TabLabel,
                DisplayName,
                ProviderStatus.Offline,
                Array.Empty<QuotaLane>(),
                now,
                StatusMessage: "Status bridge file invalid or unreadable");
            return true;
        }
    }

    internal static bool HasExpiredReset(ProviderSnapshot snapshot, DateTimeOffset now) =>
        snapshot.Lanes.Any(lane => lane.ResetsAt is { } resetsAt && resetsAt <= now);

    private bool TryReadAccountKey(out string? accountKey)
    {
        accountKey = null;
        if (!isExternalAgyRunning() || !File.Exists(statusFilePath)) return false;

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

    public static bool IsExternalAgyCliRunning(int excludedProcessId = 0) =>
        IsProcessRunning("agy", excludedProcessId);

    public static bool IsAntigravityDesktopRunning() =>
        IsProcessRunning("antigravity", excludedProcessId: 0);

    private static bool IsProcessRunning(string expectedProcessName, int excludedProcessId)
    {
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (excludedProcessId > 0 && process.Id == excludedProcessId)
                        continue;

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
        disposed = true;
    }
}
