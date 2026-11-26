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

    public static string BuildBridgeCommand(string executablePath) =>
        $"\"{executablePath}\" {BridgeArgument}";

    public static BridgeSetupResult ConfigureCliSettings(string executablePath, out string? existingCommand) =>
        ConfigureCliSettingsAt(GetCliSettingsPath(), executablePath, out existingCommand);

    internal static BridgeSetupResult ConfigureCliSettingsAt(
        string settingsPath,
        string executablePath,
        out string? existingCommand)
    {
        existingCommand = null;
        var expectedCommand = BuildBridgeCommand(executablePath);

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

        if (root["statusLine"] is JsonObject existingStatusLine &&
            existingStatusLine["command"]?.GetValue<string>() is { } currentCmd &&
            !string.IsNullOrWhiteSpace(currentCmd))
        {
            if (currentCmd.Contains(BridgeArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(currentCmd.Trim(), expectedCommand, StringComparison.OrdinalIgnoreCase) &&
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

        root["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = expectedCommand,
            ["padding"] = 0,
            ["enabled"] = true,
            ["stack_with_default"] = true
        };

        var formatted = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteAtomic(settingsPath, formatted);
        return BridgeSetupResult.Configured;
    }

    internal static void WriteAtomic(string destinationPath, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
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
