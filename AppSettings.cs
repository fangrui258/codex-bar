using System.Text.Json;
using Microsoft.Win32;

namespace CodexBar;

internal sealed class AppSettings
{
    public double Opacity { get; set; } = 0.9;
    public bool AlwaysOnTop { get; set; } = true;
    public int RefreshSeconds { get; set; } = 15;
    public bool NotifyOnUsageLimitReached { get; set; } = true;
    public string? UsageLimitNotificationEmail { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? SmtpFromAddress { get; set; }
    public DateTimeOffset? LastUsageLimitNotificationResetAt { get; set; }
    public DateTimeOffset? LastObservedWeeklyResetAt { get; set; }
    public double? LastObservedWeeklyUsedPercent { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBar", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch { /* A display preference should never crash the widget. */ }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("CodexBar") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value)
                key.SetValue("CodexBar", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("CodexBar", false);
        }
    }
}
