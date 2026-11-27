using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexBar;

internal enum BridgeSetupResult
{
    Configured,
    AlreadyConfigured,
    CustomCommandConflict
}

internal static class AntigravityStatusBridge
{
    public const string BridgeArgument = "--antigravity-statusline";
    private const string ShimFileName = "codexbar-antigravity-statusline.cmd";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    public static int RunFromStandardInput() =>
        Run(Console.In, AntigravityUsageProvider.ResolveStatusFilePath());

    internal static int Run(TextReader reader, string destinationPath)
    {
        try
        {
            var payload = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(payload))
                return 0;

            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return 0;

            WriteAtomic(destinationPath, payload);
        }
        catch
        {
            // Never disrupt the agy CLI status line output on transient bridge errors.
        }

        return 0;
    }

    public static string GetCliSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini",
        "antigravity-cli",
        "settings.json");

    public static string GetBridgeShimPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBar",
        ShimFileName);

    public static string BuildBridgeCommand(string executablePath) =>
        ResolveCommandPath(GetBridgeShimPath());

    public static string BuildDirectCommand(string executablePath) =>
        $"\"{executablePath}\" {BridgeArgument}";

    public static bool IsBridgeConfigured() => IsBridgeConfiguredAt(GetCliSettingsPath());

    internal static bool IsBridgeConfiguredAt(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            var raw = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var root = JsonNode.Parse(raw) as JsonObject;
            if (root?["statusLine"] is not JsonObject statusLine) return false;
            if (statusLine["enabled"]?.GetValue<bool>() == false) return false;

            var command = statusLine["command"]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(command) && IsManagedCommand(command);
        }
        catch
        {
            return false;
        }
    }

    public static BridgeSetupResult ConfigureCliSettings(string executablePath, out string? existingCommand) =>
        ConfigureCliSettingsAt(GetCliSettingsPath(), executablePath, out existingCommand, GetBridgeShimPath());

    internal static BridgeSetupResult ConfigureCliSettingsAt(
        string settingsPath,
        string executablePath,
        out string? existingCommand,
        string? shimPath = null)
    {
        existingCommand = null;
        var targetShimPath = shimPath ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(settingsPath))!,
            ShimFileName);

        JsonObject root;
        if (File.Exists(settingsPath))
        {
            var raw = File.ReadAllText(settingsPath);
            root = string.IsNullOrWhiteSpace(raw)
                ? new JsonObject()
                : JsonNode.Parse(raw) as JsonObject
                    ?? throw new InvalidOperationException("Antigravity CLI settings.json is not a JSON object.");
        }
        else
        {
            root = new JsonObject();
        }

        var shimContent = BuildShimContent(executablePath);
        if (root["statusLine"] is JsonObject existingStatusLine &&
            existingStatusLine["command"]?.GetValue<string>() is { } currentCmd &&
            !string.IsNullOrWhiteSpace(currentCmd))
        {
            if (IsManagedCommand(currentCmd))
            {
                WriteAtomic(targetShimPath, shimContent);
                var expectedCmd = ResolveCommandPath(targetShimPath);
                if (string.Equals(currentCmd.Trim(), expectedCmd, StringComparison.OrdinalIgnoreCase) &&
                    existingStatusLine["enabled"]?.GetValue<bool>() != false)
                {
                    return BridgeSetupResult.AlreadyConfigured;
                }
            }
            else
            {
                existingCommand = currentCmd;
                return BridgeSetupResult.CustomCommandConflict;
            }
        }
        else
        {
            WriteAtomic(targetShimPath, shimContent);
        }

        var command = ResolveCommandPath(targetShimPath);
        root["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["padding"] = 0,
            ["enabled"] = true,
            ["stack_with_default"] = true
        };

        var formatted = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteAtomic(settingsPath, formatted);
        return BridgeSetupResult.Configured;
    }

    private static bool IsManagedCommand(string command) =>
        command.Contains("antigravity-statusline", StringComparison.OrdinalIgnoreCase);

    internal static string BuildShimContent(string executablePath) =>
        $"@echo off\r\nchcp 65001 >nul 2>nul\r\n\"{executablePath}\" {BridgeArgument}\r\n";

    private static string ResolveCommandPath(string path)
    {
        if (!path.Contains(' ', StringComparison.Ordinal))
            return path;

        var buffer = new StringBuilder(260);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        if (length > buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length);
            length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        }

        if (length > 0)
        {
            var shortPath = buffer.ToString();
            if (!shortPath.Contains(' ', StringComparison.Ordinal))
                return shortPath;
        }

        return path;
    }

    internal static void WriteAtomic(string destinationPath, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content, Utf8NoBom);
            if (File.Exists(destinationPath))
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, destinationPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }
}
