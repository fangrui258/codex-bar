using System.Globalization;
using System.Text.Json;
using CodexBar;

var tests = new (string Name, Func<Task> Run)[]
{
    ("parses five-hour and weekly windows by duration", Sync(ParsesBothWindows)),
    ("does not depend on primary/secondary ordering", Sync(ParsesReversedWindows)),
    ("prefers the account-wide codex bucket", Sync(PrefersCodexBucket)),
    ("keeps weekly usage when five-hour usage is absent", Sync(AllowsMissingFiveHourWindow)),
    ("clamps percentages and preserves available reset credits", Sync(ClampsAndParsesResetCredits)),
    ("rejects a response without weekly usage", Sync(RejectsMissingWeeklyWindow)),
    ("detects an external Codex authentication change once", Sync(DetectsAuthenticationChange)),
    ("parses Antigravity statusLine quota buckets and reset times", Sync(ParsesAntigravityStatusLinePayload)),
    ("derives Antigravity reset time from reset_in_seconds fallback", Sync(ParsesAntigravityResetInSecondsFallback)),
    ("handles Antigravity payload without quota buckets", Sync(HandlesEmptyAntigravityQuota)),
    ("returns Antigravity standby when client is not running", ReturnsAntigravityStandbyWhenStopped),
    ("reads Antigravity live file and detects account switches when running", ReadsAntigravityLiveFileAndAccountSwitch),
    ("writes Antigravity statusLine bridge safely and respects custom scripts", Sync(ConfiguresAndPreservesAntigravityCliSettings)),
    ("migrates legacy Codex notification state into multi-provider settings", Sync(MigratesLegacyNotificationSettings)),
    ("ignores epoch placeholder reset_time and zero reset_in_seconds", Sync(IgnoresEpochPlaceholderResetTimes)),
    ("distinguishes desktop standby and stale expired snapshots", DistinguishesDesktopStandbyAndStaleExpiredSnapshots),
    ("reports Antigravity CLI bridge readiness from settings.json", Sync(DetectsBridgeConfigurationReadiness)),
    ("parses agy -p /usage JSON groups and buckets", Sync(ParsesAgyPrintUsageJson)),
    ("rejects non-SUCCESS agy -p /usage status", Sync(RejectsNonSuccessAgyPrintJson)),
    ("polls agy CLI automatically when desktop app is running and caches interval", PollsCliAutomaticallyWhenDesktopRunningAndRespectsCache),
    ("runs console commands inside an isolated hidden Win32 desktop", RunsConsoleCommandOnHiddenDesktop)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

if (args is ["--snapshot", var snapshotPath])
    SaveVisualSnapshot(snapshotPath);
else if (args is ["--live"])
    await VerifyLiveUsageAsync();
else if (args is ["--live-antigravity"])
    await VerifyLiveAntigravityUsageAsync();

return failures == 0 ? 0 : 1;

static Func<Task> Sync(Action action) => () =>
{
    action();
    return Task.CompletedTask;
};

static void ParsesBothWindows()
{
    var usage = Parse("""
        {
          "result": {
            "rateLimits": {
              "limitId": "codex",
              "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1787700000 },
              "secondary": { "usedPercent": 60, "windowDurationMins": 10080, "resetsAt": 1788300000 }
            }
          }
        }
        """);

    var fiveHour = Required(usage.FiveHour, "five-hour window");
    Equal(25d, fiveHour.UsedPercent, "five-hour used percent");
    Equal(DateTimeOffset.FromUnixTimeSeconds(1787700000), fiveHour.ResetsAt, "five-hour reset");
    Equal(60d, usage.Weekly.UsedPercent, "weekly used percent");
    Equal(DateTimeOffset.FromUnixTimeSeconds(1788300000), usage.Weekly.ResetsAt, "weekly reset");
}

static void ParsesReversedWindows()
{
    var usage = Parse("""
        {
          "result": {
            "rateLimits": {
              "limitId": "codex",
              "primary": { "usedPercent": 71, "windowDurationMins": 10080, "resetsAt": 1788300000 },
              "secondary": { "usedPercent": 13, "windowDurationMins": 300, "resetsAt": 1787700000 }
            }
          }
        }
        """);

    Equal(13d, Required(usage.FiveHour, "five-hour window").UsedPercent, "five-hour used percent");
    Equal(71d, usage.Weekly.UsedPercent, "weekly used percent");
}

static void PrefersCodexBucket()
{
    var usage = Parse("""
        {
          "result": {
            "rateLimits": {
              "limitId": "codex_other",
              "primary": { "usedPercent": 99, "windowDurationMins": 300, "resetsAt": 1787700000 }
            },
            "rateLimitsByLimitId": {
              "codex": {
                "limitId": "codex",
                "primary": { "usedPercent": 10, "windowDurationMins": 300, "resetsAt": 1787700000 },
                "secondary": { "usedPercent": 20, "windowDurationMins": 10080, "resetsAt": 1788300000 }
              }
            }
          }
        }
        """);

    Equal(10d, Required(usage.FiveHour, "five-hour window").UsedPercent, "five-hour used percent");
    Equal(20d, usage.Weekly.UsedPercent, "weekly used percent");
}

static void AllowsMissingFiveHourWindow()
{
    var usage = Parse("""
        {
          "result": {
            "rateLimits": {
              "limitId": "codex",
              "primary": { "usedPercent": 40, "windowDurationMins": 10080, "resetsAt": 1788300000 }
            }
          }
        }
        """);

    Equal<UsageWindow?>(null, usage.FiveHour, "five-hour window");
    Equal(40d, usage.Weekly.UsedPercent, "weekly used percent");
}

static void ClampsAndParsesResetCredits()
{
    var usage = Parse("""
        {
          "result": {
            "rateLimits": {
              "limitId": "codex",
              "primary": { "usedPercent": -5, "windowDurationMins": 300, "resetsAt": 1787700000 },
              "secondary": { "usedPercent": 120, "windowDurationMins": 10080, "resetsAt": 1788300000 }
            },
            "rateLimitResetCredits": {
              "credits": [
                { "status": "available", "expiresAt": 1789000000 },
                { "status": "redeemed", "expiresAt": 1787000000 },
                { "status": "available", "expiresAt": 1788000000 }
              ]
            }
          }
        }
        """);

    Equal(0d, Required(usage.FiveHour, "five-hour window").UsedPercent, "clamped five-hour used percent");
    Equal(100d, usage.Weekly.UsedPercent, "clamped weekly used percent");
    Equal(2, usage.ResetExpirations.Count, "available reset count");
    Equal(DateTimeOffset.FromUnixTimeSeconds(1788000000), usage.ResetExpirations[0], "first reset expiration");
    Equal(DateTimeOffset.FromUnixTimeSeconds(1789000000), usage.ResetExpirations[1], "second reset expiration");
}

static void RejectsMissingWeeklyWindow()
{
    try
    {
        Parse("""
            {
              "result": {
                "rateLimits": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 40, "windowDurationMins": 300, "resetsAt": 1787700000 }
                }
              }
            }
            """);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("weekly", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    throw new InvalidOperationException("Expected a missing-weekly-window error.");
}

static UsageSnapshot Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    return UsageRateParser.Parse(document.RootElement);
}

static UsageWindow Required(UsageWindow? window, string label) =>
    window ?? throw new InvalidOperationException($"Expected {label} to be present.");

static void DetectsAuthenticationChange()
{
    var directory = Path.Combine(Path.GetTempPath(), $"codexbar-auth-{Guid.NewGuid():N}");
    var authPath = Path.Combine(directory, "auth.json");
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(authPath, "account-one");
        var originalWriteTime = File.GetLastWriteTimeUtc(authPath);
        var monitor = new AuthenticationFileMonitor(authPath);

        Equal(false, monitor.HasChanged(), "initial observation");
        File.WriteAllText(authPath, "account-two-with-a-different-size");
        File.SetLastWriteTimeUtc(authPath, originalWriteTime.AddSeconds(1));
        Equal(true, monitor.HasChanged(), "changed authentication");
        Equal(false, monitor.HasChanged(), "unchanged authentication after restart");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ParsesAntigravityStatusLinePayload()
{
    using var doc = JsonDocument.Parse("""
        {
          "product": "antigravity",
          "email": "dev@example.com",
          "plan_tier": "Pro",
          "quota": {
            "gemini-pro": {
              "remaining_fraction": 0.85,
              "reset_time": "2026-11-27T00:00:00Z",
              "reset_in_seconds": 18000
            },
            "claude": {
              "remaining_fraction": 0.42,
              "reset_time": "2026-11-27T02:00:00Z"
            },
            "gemini-weekly": {
              "remaining_fraction": 1.25,
              "reset_time": "2026-12-01T00:00:00Z"
            }
          }
        }
        """);

    var snapshot = AntigravityUsageParser.Parse(doc.RootElement, DateTimeOffset.UnixEpoch);
    Equal(ProviderStatus.Online, snapshot.Status, "status");
    Equal("dev@example.com", snapshot.AccountKey, "account key");
    Equal(3, snapshot.Lanes.Count, "lane count");
    Equal("GEMINI PRO LEFT", snapshot.Lanes[0].Label, "first lane label");
    CloseTo(85d, snapshot.Lanes[0].RemainingPercent!.Value, "gemini pro remaining");
    Equal(DateTimeOffset.Parse("2026-11-27T00:00:00Z", CultureInfo.InvariantCulture), snapshot.Lanes[0].ResetsAt, "gemini pro reset");
    Equal("CLAUDE LEFT", snapshot.Lanes[1].Label, "second lane label");
    CloseTo(42d, snapshot.Lanes[1].RemainingPercent!.Value, "claude remaining");
    Equal("GEMINI WEEKLY LEFT", snapshot.Lanes[2].Label, "third lane label");
    CloseTo(100d, snapshot.Lanes[2].RemainingPercent!.Value, "clamped remaining");
}

static void ParsesAntigravityResetInSecondsFallback()
{
    var observedAt = DateTimeOffset.Parse("2026-11-26T12:00:00Z", CultureInfo.InvariantCulture);
    using var doc = JsonDocument.Parse("""
        {
          "quota": {
            "gemini-flash": {
              "remaining_fraction": 0.75,
              "reset_in_seconds": 3600
            }
          }
        }
        """);

    var snapshot = AntigravityUsageParser.Parse(doc.RootElement, observedAt);
    Equal(1, snapshot.Lanes.Count, "lane count");
    CloseTo(75d, snapshot.Lanes[0].RemainingPercent!.Value, "remaining percent");
    Equal(observedAt.AddSeconds(3600), snapshot.Lanes[0].ResetsAt, "computed reset time");
}

static void HandlesEmptyAntigravityQuota()
{
    using var doc = JsonDocument.Parse("""
        {
          "product": "antigravity",
          "model": { "id": "Gemini 4" }
        }
        """);

    var snapshot = AntigravityUsageParser.Parse(doc.RootElement, DateTimeOffset.UtcNow);
    Equal(ProviderStatus.Online, snapshot.Status, "status");
    Equal(0, snapshot.Lanes.Count, "empty lanes");
    if (string.IsNullOrWhiteSpace(snapshot.StatusMessage))
        throw new InvalidOperationException("Expected explanatory status message when quota is empty.");
}

static async Task ReturnsAntigravityStandbyWhenStopped()
{
    var directory = Path.Combine(Path.GetTempPath(), $"codexbar-agy-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var statusPath = Path.Combine(directory, "antigravity-status.json");
        File.WriteAllText(statusPath, """{"quota":{"gemini-pro":{"remaining_fraction":0.5}}}""");

        using var provider = new AntigravityUsageProvider(statusPath, () => false);
        var snapshot = await provider.ReadUsageAsync();
        Equal(ProviderStatus.Standby, snapshot.Status, "standby status");
        Equal(0, snapshot.Lanes.Count, "no live lanes in standby");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ReadsAntigravityLiveFileAndAccountSwitch()
{
    var directory = Path.Combine(Path.GetTempPath(), $"codexbar-agy-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var statusPath = Path.Combine(directory, "antigravity-status.json");
        var fixedNow = DateTimeOffset.Parse("2026-11-26T22:00:00Z", CultureInfo.InvariantCulture);
        using var provider = new AntigravityUsageProvider(statusPath, () => true, () => false, () => fixedNow);

        var missing = await provider.ReadUsageAsync();
        Equal(ProviderStatus.Offline, missing.Status, "offline before bridge writes");

        using (var reader = new StringReader("""
            {"email":"one@example.com","quota":{"gemini-pro":{"remaining_fraction":0.6,"reset_time":"2026-11-27T01:00:00Z"}}}
            """))
        {
            Equal(0, AntigravityStatusBridge.Run(reader, statusPath), "bridge exit code");
        }

        var first = await provider.ReadUsageAsync();
        Equal(ProviderStatus.Online, first.Status, "online after bridge write");
        Equal(false, provider.CheckAccountChanged(), "same account");
        CloseTo(60d, first.Lanes[0].RemainingPercent!.Value, "remaining percent");

        using (var reader = new StringReader("""
            {"email":"two@example.com","quota":{"gemini-pro":{"remaining_fraction":0.95,"reset_time":"2026-11-27T04:00:00Z"}}}
            """))
        {
            Equal(0, AntigravityStatusBridge.Run(reader, statusPath), "second bridge write");
        }

        Equal(true, provider.CheckAccountChanged(), "detected account change");
        Equal(false, provider.CheckAccountChanged(), "account change cleared");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ConfiguresAndPreservesAntigravityCliSettings()
{
    var directory = Path.Combine(Path.GetTempPath(), $"codexbar-agysettings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        var shimPath = Path.Combine(directory, "codexbar-antigravity-statusline.cmd");
        var exePath = @"D:\浏览器下载\CodexBar-win-x64 (2).exe";

        var first = AntigravityStatusBridge.ConfigureCliSettingsAt(settingsPath, exePath, out _, shimPath);
        Equal(BridgeSetupResult.Configured, first, "initial bridge setup");
        if (!File.Exists(shimPath))
            throw new InvalidOperationException("Expected batch shim file to be created.");
        var shimText = File.ReadAllText(shimPath);
        if (!shimText.Contains("chcp 65001", StringComparison.Ordinal) ||
            !shimText.Contains(exePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Expected batch shim to invoke executable under UTF-8.");

        var second = AntigravityStatusBridge.ConfigureCliSettingsAt(settingsPath, exePath, out _, shimPath);
        Equal(BridgeSetupResult.AlreadyConfigured, second, "idempotent bridge setup");

        var legacyJson = JsonSerializer.Serialize(new
        {
            statusLine = new { type = "command", command = $"\"{exePath}\" --antigravity-statusline" }
        });
        File.WriteAllText(settingsPath, legacyJson);
        var upgraded = AntigravityStatusBridge.ConfigureCliSettingsAt(settingsPath, exePath, out _, shimPath);
        Equal(BridgeSetupResult.Configured, upgraded, "upgrades legacy quoted bridge command to shim");

        File.WriteAllText(settingsPath, """{"statusLine":{"type":"command","command":"my-custom-statusline.cmd"}}""");
        var third = AntigravityStatusBridge.ConfigureCliSettingsAt(settingsPath, exePath, out var existing, shimPath);
        Equal(BridgeSetupResult.CustomCommandConflict, third, "custom command preserved");
        Equal("my-custom-statusline.cmd", existing, "reported existing command");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void MigratesLegacyNotificationSettings()
{
    var directory = Path.Combine(Path.GetTempPath(), $"codexbar-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "settings.json");
        var resetAt = DateTimeOffset.Parse("2026-12-01T00:00:00Z", CultureInfo.InvariantCulture);
        File.WriteAllText(path, $$"""
            {
              "LastObservedWeeklyUsedPercent": 42.5,
              "LastUsageLimitNotificationResetAt": "{{resetAt:O}}"
            }
            """);

        var settings = AppSettings.LoadFrom(path);
        Equal(CodexUsageProvider.ProviderId, settings.ActiveProvider, "default active provider");
        var migrated = settings.GetOrCreateNotificationState(CodexUsageProvider.ProviderId, "weekly");
        Equal(42.5d, migrated.LastObservedUsedPercent, "migrated used percent");
        Equal(resetAt, migrated.LastNotifiedResetAt, "migrated reset timestamp");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void IgnoresEpochPlaceholderResetTimes()
{
    using var doc = JsonDocument.Parse("""
        {
          "quota": {
            "3p-5h": {
              "remaining_fraction": 1,
              "reset_time": "1970-01-01T00:00:00Z",
              "reset_in_seconds": 0
            },
            "gemini-5h": {
              "remaining_fraction": 0.92,
              "reset_time": "2026-11-27T08:00:00Z"
            }
          }
        }
        """);

    var snapshot = AntigravityUsageParser.Parse(doc.RootElement, DateTimeOffset.Parse("2026-11-27T04:00:00Z", CultureInfo.InvariantCulture));
    Equal(2, snapshot.Lanes.Count, "lane count");
    Equal<DateTimeOffset?>(null, snapshot.Lanes[0].ResetsAt, "epoch placeholder ignored");
    Equal(DateTimeOffset.Parse("2026-11-27T08:00:00Z", CultureInfo.InvariantCulture), snapshot.Lanes[1].ResetsAt, "valid reset kept");
}

static async Task DistinguishesDesktopStandbyAndStaleExpiredSnapshots()
{
    var directory = Path.Combine(Path.GetTempPath(), $"codexbar-agystale-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var statusPath = Path.Combine(directory, "antigravity-status.json");
        File.WriteAllText(statusPath, """
            {
              "email": "dev@example.com",
              "quota": {
                "gemini-5h": {
                  "remaining_fraction": 0.92,
                  "reset_time": "2026-11-26T17:04:13Z"
                }
              }
            }
            """);
        File.SetLastWriteTimeUtc(statusPath, DateTime.SpecifyKind(DateTime.Parse("2026-11-26T12:57:05Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc));

        using var desktopOnlyProvider = new AntigravityUsageProvider(
            statusPath,
            isExternalAgyRunning: () => false,
            isDesktopRunning: () => true,
            utcNowProvider: () => DateTimeOffset.Parse("2026-11-27T02:28:00Z", CultureInfo.InvariantCulture));
        var desktopOnlySnapshot = await desktopOnlyProvider.ReadUsageAsync();
        Equal(ProviderStatus.Standby, desktopOnlySnapshot.Status, "desktop-only standby");
        Equal(0, desktopOnlySnapshot.Lanes.Count, "no stale lanes when agy stopped");
        if (desktopOnlySnapshot.StatusMessage?.Contains("agy", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("Expected status message to guide running agy CLI.");

        using var staleAgyProvider = new AntigravityUsageProvider(
            statusPath,
            isExternalAgyRunning: () => true,
            isDesktopRunning: () => true,
            utcNowProvider: () => DateTimeOffset.Parse("2026-11-27T02:28:00Z", CultureInfo.InvariantCulture));
        var staleSnapshot = await staleAgyProvider.ReadUsageAsync();
        Equal(ProviderStatus.Standby, staleSnapshot.Status, "stale expired snapshot rejected");
        Equal(0, staleSnapshot.Lanes.Count, "no expired lanes displayed");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void DetectsBridgeConfigurationReadiness()
{
    var directory = Path.Combine(Path.GetTempPath(), $"codexbar-agyready-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        var shimPath = Path.Combine(directory, "codexbar-antigravity-statusline.cmd");
        Equal(false, AntigravityStatusBridge.IsBridgeConfiguredAt(settingsPath), "unconfigured when missing");

        AntigravityStatusBridge.ConfigureCliSettingsAt(settingsPath, @"C:\Tools\CodexBar.exe", out _, shimPath);
        Equal(true, AntigravityStatusBridge.IsBridgeConfiguredAt(settingsPath), "configured after setup");

        File.WriteAllText(settingsPath, """{"statusLine":{"type":"command","command":"other-script.cmd","enabled":true}}""");
        Equal(false, AntigravityStatusBridge.IsBridgeConfiguredAt(settingsPath), "not managed when custom script");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ParsesAgyPrintUsageJson()
{
    using var doc = JsonDocument.Parse("""
        {
          "conversation_id": "",
          "status": "SUCCESS",
          "duration_seconds": 0,
          "num_turns": 0,
          "usage": { "total_tokens": 0 },
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Gemini Models",
                  "buckets": [
                    { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.8664, "reset_time": "2026-12-03T07:04:13Z" },
                    { "id": "gemini-5h", "window": "5h", "remaining_fraction": 0.9899, "reset_time": "2026-11-27T13:57:18Z" }
                  ]
                },
                {
                  "name": "Claude and GPT models",
                  "buckets": [
                    { "id": "3p-weekly", "window": "weekly", "remaining_fraction": 1, "reset_time": "1970-01-01T00:00:00Z" },
                    { "id": "3p-5h", "window": "5h", "remaining_fraction": 1, "reset_time": "1970-01-01T00:00:00Z" }
                  ]
                }
              ]
            }
          }
        }
        """);

    var snapshot = AntigravityUsageParser.Parse(doc.RootElement, DateTimeOffset.Parse("2026-11-27T09:00:00Z", CultureInfo.InvariantCulture));
    Equal(ProviderStatus.Online, snapshot.Status, "online status");
    Equal(4, snapshot.Lanes.Count, "four quota lanes");
    Equal("GEMINI WEEKLY LEFT", snapshot.Lanes[0].Label, "gemini weekly label");
    CloseTo(86.64d, snapshot.Lanes[0].RemainingPercent!.Value, "gemini weekly remaining");
    Equal("GEMINI 5H LEFT", snapshot.Lanes[1].Label, "gemini 5h label");
    CloseTo(98.99d, snapshot.Lanes[1].RemainingPercent!.Value, "gemini 5h remaining");
    Equal("CLAUDE/GPT WEEKLY LEFT", snapshot.Lanes[2].Label, "3p weekly label");
    Equal<DateTimeOffset?>(null, snapshot.Lanes[2].ResetsAt, "3p weekly epoch ignored");
    Equal("CLAUDE/GPT 5H LEFT", snapshot.Lanes[3].Label, "3p 5h label");
    Equal<DateTimeOffset?>(null, snapshot.Lanes[3].ResetsAt, "3p 5h epoch ignored");
}

static void RejectsNonSuccessAgyPrintJson()
{
    using var doc = JsonDocument.Parse("""{"status":"ERROR","response":"Not signed in"}""");
    try
    {
        AntigravityUsageParser.Parse(doc.RootElement, DateTimeOffset.UtcNow);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("ERROR", StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException("Expected an error for non-SUCCESS status.");
}

static async Task PollsCliAutomaticallyWhenDesktopRunningAndRespectsCache()
{
    var now = DateTimeOffset.Parse("2026-11-27T09:00:00Z", CultureInfo.InvariantCulture);
    var desktopRunning = true;
    var fetchCount = 0;

    using var provider = new AntigravityUsageProvider(
        statusFilePath: Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.json"),
        isExternalAgyRunning: () => false,
        isDesktopRunning: () => desktopRunning,
        utcNowProvider: () => now,
        cliUsageFetcher: _ =>
        {
            fetchCount++;
            return Task.FromResult(new ProviderSnapshot(
                AntigravityUsageProvider.ProviderId,
                "ANTIGRAVITY",
                "Antigravity",
                ProviderStatus.Online,
                new[] { new QuotaLane("gemini-weekly", "GEMINI WEEKLY LEFT", 10 + fetchCount, now.AddDays(5)) },
                now));
        },
        cliPollInterval: TimeSpan.FromSeconds(30));

    var first = await provider.ReadUsageAsync(forceRefresh: false);
    Equal(ProviderStatus.Online, first.Status, "online when desktop app open");
    Equal(1, fetchCount, "initial CLI poll");

    now = now.AddSeconds(10);
    var cached = await provider.ReadUsageAsync(forceRefresh: false);
    Equal(1, fetchCount, "cached within poll interval");
    CloseTo(first.Lanes[0].RemainingPercent!.Value, cached.Lanes[0].RemainingPercent!.Value, "cached percentage");

    var forced = await provider.ReadUsageAsync(forceRefresh: true);
    Equal(2, fetchCount, "forced refresh bypasses cache");
    CloseTo(88d, forced.Lanes[0].RemainingPercent!.Value, "updated forced percentage");

    desktopRunning = false;
    var standby = await provider.ReadUsageAsync(forceRefresh: false);
    Equal(ProviderStatus.Standby, standby.Status, "standby when desktop closes");
    Equal(2, fetchCount, "no CLI poll in standby");
}

static async Task RunsConsoleCommandOnHiddenDesktop()
{
    var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    var result = await HiddenDesktopProcessRunner.RunAsync(
        cmdExe,
        new[] { "/c", "echo hidden-desktop-ok" },
        Path.GetTempPath(),
        onProcessStarted: null,
        CancellationToken.None);

    Equal(0, result.ExitCode, "hidden desktop cmd exit code");
    if (!result.StandardOutput.Contains("hidden-desktop-ok", StringComparison.Ordinal))
        throw new InvalidOperationException($"Unexpected stdout: {result.StandardOutput}");

    var commandLine = HiddenDesktopProcessRunner.BuildCommandLine(
        @"C:\Path With Spaces\agy.exe",
        new[] { "-p", "/usage", "--output-format", "json" });
    Equal("\"C:\\Path With Spaces\\agy.exe\" -p /usage --output-format json", commandLine, "command line quoting");
}

static void SaveVisualSnapshot(string path)
{
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
    CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    using var form = new WidgetForm(new IUsageProvider[] { new CodexUsageProvider(), new AntigravityUsageProvider() });
    var now = DateTimeOffset.UtcNow;

    var codexUsage = CodexUsageProvider.ToProviderSnapshot(new UsageSnapshot(
        new UsageWindow(37, now.AddHours(2)),
        new UsageWindow(58, now.AddDays(3)),
        now,
        new[] { now.AddDays(10), now.AddDays(17) }));

    var agyOnline = new ProviderSnapshot(
        AntigravityUsageProvider.ProviderId,
        "ANTIGRAVITY",
        "Antigravity",
        ProviderStatus.Online,
        new[]
        {
            new QuotaLane("3p-5h", "CLAUDE/GPT 5H LEFT", 0, null),
            new QuotaLane("gemini-5h", "GEMINI 5H LEFT", 3, now.AddHours(3.25)),
            new QuotaLane("gemini-weekly", "GEMINI WEEKLY LEFT", 10, now.AddDays(6.2))
        },
        now);

    var agyStandby = new ProviderSnapshot(
        AntigravityUsageProvider.ProviderId,
        "ANTIGRAVITY",
        "Antigravity",
        ProviderStatus.Standby,
        Array.Empty<QuotaLane>(),
        now,
        "Desktop open · run agy CLI to sync");

    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    Directory.CreateDirectory(outputDirectory);
    var stem = Path.GetFileNameWithoutExtension(path);
    var ext = Path.GetExtension(path);

    form.SetSnapshotForTest(codexUsage);
    form.SwitchProvider(CodexUsageProvider.ProviderId, persist: false);
    RenderFormToPng(form, Path.Combine(outputDirectory, $"{stem}{ext}"));

    form.SetSnapshotForTest(agyOnline);
    form.SwitchProvider(AntigravityUsageProvider.ProviderId, persist: false);
    RenderFormToPng(form, Path.Combine(outputDirectory, $"{stem}-antigravity{ext}"));

    form.SetSnapshotForTest(agyStandby);
    form.SwitchProvider(AntigravityUsageProvider.ProviderId, persist: false);
    RenderFormToPng(form, Path.Combine(outputDirectory, $"{stem}-antigravity-standby{ext}"));
}

static void RenderFormToPng(WidgetForm form, string targetPath)
{
    form.CreateControl();
    using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
    form.DrawToBitmap(bitmap, form.ClientRectangle);
    bitmap.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine($"SNAPSHOT {Path.GetFullPath(targetPath)}");
}

static async Task VerifyLiveUsageAsync()
{
    using var client = new CodexAppServerClient();
    var usage = await client.ReadUsageAsync();
    var fiveHour = Required(usage.FiveHour, "live five-hour window");
    Console.WriteLine($"LIVE 5-hour left: {100 - fiveHour.UsedPercent:0}%");
    Console.WriteLine($"LIVE weekly left: {100 - usage.Weekly.UsedPercent:0}%");
}

static async Task VerifyLiveAntigravityUsageAsync()
{
    using var provider = new AntigravityUsageProvider();
    var snapshot = await provider.ReadUsageAsync(forceRefresh: true);
    Console.WriteLine($"LIVE ANTIGRAVITY status: {snapshot.Status} ({snapshot.StatusMessage ?? "ok"})");
    foreach (var lane in snapshot.Lanes)
        Console.WriteLine($"LIVE ANTIGRAVITY {lane.Label}: {lane.RemainingPercent:0}% (resets: {lane.ResetsAt?.ToLocalTime().ToString("g") ?? "Full capacity"})");
}

static void CloseTo(double expected, double actual, string label)
{
    if (Math.Abs(expected - actual) > 0.001)
        throw new InvalidOperationException($"Expected {label} to be {expected}, got {actual}.");
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {label} to be {expected}, got {actual}.");
}
