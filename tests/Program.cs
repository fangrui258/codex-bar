using System.Text.Json;
using System.Reflection;
using System.Globalization;
using CodexBar;

var tests = new (string Name, Action Run)[]
{
    ("parses five-hour and weekly windows by duration", ParsesBothWindows),
    ("does not depend on primary/secondary ordering", ParsesReversedWindows),
    ("prefers the account-wide codex bucket", PrefersCodexBucket),
    ("keeps weekly usage when five-hour usage is absent", AllowsMissingFiveHourWindow),
    ("clamps percentages and preserves available reset credits", ClampsAndParsesResetCredits),
    ("rejects a response without weekly usage", RejectsMissingWeeklyWindow)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
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

return failures == 0 ? 0 : 1;

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

static void SaveVisualSnapshot(string path)
{
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
    CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    using var form = new WidgetForm();
    var now = DateTimeOffset.UtcNow;
    var usage = new UsageSnapshot(
        new UsageWindow(37, now.AddHours(2)),
        new UsageWindow(58, now.AddDays(3)),
        now,
        new[] { now.AddDays(10), now.AddDays(17) });

    typeof(WidgetForm).GetField("snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(form, usage);
    typeof(WidgetForm).GetField("liveConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(form, true);

    form.CreateControl();
    using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
    form.DrawToBitmap(bitmap, form.ClientRectangle);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine($"SNAPSHOT {Path.GetFullPath(path)}");
}

static async Task VerifyLiveUsageAsync()
{
    using var client = new CodexAppServerClient();
    var usage = await client.ReadUsageAsync();
    var fiveHour = Required(usage.FiveHour, "live five-hour window");
    Console.WriteLine($"LIVE 5-hour left: {100 - fiveHour.UsedPercent:0}%");
    Console.WriteLine($"LIVE weekly left: {100 - usage.Weekly.UsedPercent:0}%");
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {label} to be {expected}, got {actual}.");
}
