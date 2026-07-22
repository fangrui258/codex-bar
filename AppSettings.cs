using System.Text.Json;
using Microsoft.Win32;

namespace CodexBar;

internal sealed class AppSettings
{
    public double Opacity { get; set; } = 0.9;
    public bool AlwaysOnTop { get; set; } = true;
    public int RefreshSeconds { get; set; } = 15;
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
