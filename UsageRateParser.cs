using System.Text.Json;

namespace CodexBar;

internal static class UsageRateParser
{
    private const int FiveHourWindowMinutes = 5 * 60;
    private const int WeeklyWindowMinutes = 7 * 24 * 60;

    public static UsageSnapshot Parse(JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Codex returned no ChatGPT rate limits. Sign in to Codex first.");

        var limits = FindAccountLimits(result);
        UsageWindow? fiveHour = null;
        UsageWindow? weekly = null;

        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var value) || !TryParseWindow(value, out var duration, out var window))
                continue;

            if (duration == FiveHourWindowMinutes)
                fiveHour = window;
            else if (duration == WeeklyWindowMinutes)
                weekly = window;
        }

        if (weekly is null)
            throw new InvalidOperationException("Codex returned no weekly rate-limit window.");

        return new UsageSnapshot(
            fiveHour,
            weekly,
            DateTimeOffset.UtcNow,
            ParseResetCreditExpirations(result));
    }

    private static JsonElement FindAccountLimits(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var limitsById) &&
            limitsById.ValueKind == JsonValueKind.Object &&
            limitsById.TryGetProperty("codex", out var codexLimits) &&
            codexLimits.ValueKind == JsonValueKind.Object)
            return codexLimits;

        if (!result.TryGetProperty("rateLimits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Codex returned no ChatGPT rate limits. Sign in to Codex first.");

        if (limits.TryGetProperty("limitId", out var limitId) &&
            limitId.ValueKind == JsonValueKind.String &&
            !string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex returned a model-specific pool instead of the account limit.");

        return limits;
    }

    private static bool TryParseWindow(
        JsonElement value,
        out int durationMinutes,
        out UsageWindow window)
    {
        durationMinutes = 0;
        window = null!;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("windowDurationMins", out var minutes) ||
            !minutes.TryGetInt32(out durationMinutes) ||
            !value.TryGetProperty("usedPercent", out var used) ||
            !used.TryGetDouble(out var usedPercent) ||
            !value.TryGetProperty("resetsAt", out var reset) ||
            !reset.TryGetInt64(out var unixReset))
            return false;

        try
        {
            window = new UsageWindow(
                Math.Clamp(usedPercent, 0, 100),
                DateTimeOffset.FromUnixTimeSeconds(unixReset));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static IReadOnlyList<DateTimeOffset> ParseResetCreditExpirations(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var summary) ||
            summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
            return Array.Empty<DateTimeOffset>();

        var expirations = new List<DateTimeOffset>();
        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object ||
                !credit.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String ||
                !string.Equals(status.GetString(), "available", StringComparison.OrdinalIgnoreCase) ||
                !credit.TryGetProperty("expiresAt", out var expiresAt) ||
                !expiresAt.TryGetInt64(out var unixExpiration))
                continue;

            try { expirations.Add(DateTimeOffset.FromUnixTimeSeconds(unixExpiration)); }
            catch (ArgumentOutOfRangeException) { }
        }

        expirations.Sort();
        return expirations;
    }
}
