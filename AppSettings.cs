using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CodexBar;

internal sealed class ProviderNotificationState
{
    public double? LastObservedUsedPercent { get; set; }
    public DateTimeOffset? LastNotifiedResetAt { get; set; }
}

internal sealed class AppSettings
{
    public double Opacity { get; set; } = 0.9;
    public bool AlwaysOnTop { get; set; } = true;
    public int RefreshSeconds { get; set; } = 15;
    public string ActiveProvider { get; set; } = CodexUsageProvider.ProviderId;
    public bool NotifyOnUsageLimitReached { get; set; } = true;
    public string? UsageLimitNotificationEmail { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    [JsonIgnore]
    public string? SmtpPassword { get; set; }
    public string? ProtectedSmtpPassword { get; set; }
    public string? SmtpFromAddress { get; set; }
    public DateTimeOffset? LastUsageLimitNotificationResetAt { get; set; }
    public double? LastObservedWeeklyUsedPercent { get; set; }
    public Dictionary<string, ProviderNotificationState> NotificationStates { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int? X { get; set; }
    public int? Y { get; set; }
    [JsonIgnore]
    private string? lastProtectedPassword;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBar", "settings.json");

    public static string BuildNotificationKey(string providerId, string laneKey) =>
        $"{providerId}:{laneKey}";

    public ProviderNotificationState GetOrCreateNotificationState(string providerId, string laneKey)
    {
        var key = BuildNotificationKey(providerId, laneKey);
        if (!NotificationStates.TryGetValue(key, out var state))
        {
            state = new ProviderNotificationState();
            NotificationStates[key] = state;
        }
        return state;
    }

    public static AppSettings Load() => LoadFrom(SettingsPath);

    internal static AppSettings LoadFrom(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.NotificationStates = settings.NotificationStates is null
                ? new Dictionary<string, ProviderNotificationState>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ProviderNotificationState>(settings.NotificationStates, StringComparer.OrdinalIgnoreCase);

            var legacyKey = BuildNotificationKey(CodexUsageProvider.ProviderId, "weekly");
            if (!settings.NotificationStates.ContainsKey(legacyKey) &&
                (settings.LastObservedWeeklyUsedPercent.HasValue || settings.LastUsageLimitNotificationResetAt.HasValue))
            {
                settings.NotificationStates[legacyKey] = new ProviderNotificationState
                {
                    LastObservedUsedPercent = settings.LastObservedWeeklyUsedPercent,
                    LastNotifiedResetAt = settings.LastUsageLimitNotificationResetAt
                };
            }

            if (string.IsNullOrWhiteSpace(settings.ActiveProvider))
                settings.ActiveProvider = CodexUsageProvider.ProviderId;

            if (!string.IsNullOrEmpty(settings.ProtectedSmtpPassword))
            {
                settings.SmtpPassword = Unprotect(settings.ProtectedSmtpPassword);
                if (settings.SmtpPassword is null)
                {
                    settings.ProtectedSmtpPassword = null;
                    settings.Save();
                }
                else
                {
                    settings.lastProtectedPassword = settings.SmtpPassword;
                }
            }
            else
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty(nameof(SmtpPassword), out var legacyPassword) &&
                    legacyPassword.ValueKind == JsonValueKind.String)
                {
                    settings.SmtpPassword = legacyPassword.GetString();
                    settings.Save();
                }
            }

            return settings;
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        string? temporaryPath = null;
        try
        {
            if (!string.Equals(SmtpPassword, lastProtectedPassword, StringComparison.Ordinal))
            {
                ProtectedSmtpPassword = string.IsNullOrEmpty(SmtpPassword) ? null : Protect(SmtpPassword);
                lastProtectedPassword = SmtpPassword;
            }

            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));

            if (File.Exists(SettingsPath))
                File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, SettingsPath);
        }
        catch { /* A display preference should never crash the widget. */ }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string Protect(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string? Unprotect(string value)
    {
        try
        {
            var protectedData = Convert.FromBase64String(value);
            var plaintext = ProtectedData.Unprotect(
                protectedData, optionalEntropy: null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
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
