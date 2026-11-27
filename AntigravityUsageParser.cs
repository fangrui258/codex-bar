using System.Globalization;
using System.Text.Json;

namespace CodexBar;

internal static class AntigravityUsageParser
{
    private static readonly DateTimeOffset MinimumValidResetTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static ProviderSnapshot Parse(JsonElement root, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Antigravity status payload must be a JSON object.");

        var email = GetOptionalString(root, "email");
        var planTier = GetOptionalString(root, "plan_tier") ?? GetOptionalString(root, "planTier");
        var accountKey = !string.IsNullOrWhiteSpace(email)
            ? email
            : !string.IsNullOrWhiteSpace(planTier) ? $"tier:{planTier}" : null;

        var lanes = new List<QuotaLane>();
        if (TryGetProperty(root, "quota", out var quota) && quota.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in quota.EnumerateObject())
            {
                if (TryParseBucket(entry.Name, entry.Value, observedAt, out var lane))
                    lanes.Add(lane);
            }
        }
        else if (TryGetArray(root, out var arrayEntries, "lanes", "buckets", "models"))
        {
            foreach (var item in arrayEntries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var key = GetOptionalString(item, "key")
                    ?? GetOptionalString(item, "id")
                    ?? GetOptionalString(item, "name");
                if (string.IsNullOrWhiteSpace(key)) continue;

                var labelOverride = GetOptionalString(item, "label");
                if (TryParseBucket(key, item, observedAt, out var lane))
                {
                    lanes.Add(string.IsNullOrWhiteSpace(labelOverride)
                        ? lane
                        : lane with { Label = labelOverride.Trim().ToUpperInvariant() });
                }
            }
        }

        if (lanes.Count == 0)
        {
            return new ProviderSnapshot(
                AntigravityUsageProvider.ProviderId,
                "ANTIGRAVITY",
                "Antigravity",
                ProviderStatus.Online,
                Array.Empty<QuotaLane>(),
                observedAt,
                StatusMessage: "No quota reported (API key or empty)",
                AccountKey: accountKey);
        }

        var alertKeys = lanes
            .Where(l => l.UsedPercent.HasValue && l.ResetsAt.HasValue)
            .Select(l => l.Key)
            .ToArray();

        return new ProviderSnapshot(
            AntigravityUsageProvider.ProviderId,
            "ANTIGRAVITY",
            "Antigravity",
            ProviderStatus.Online,
            lanes,
            observedAt,
            AccountKey: accountKey,
            AlertLaneKeys: alertKeys);
    }

    private static bool TryParseBucket(
        string key,
        JsonElement value,
        DateTimeOffset observedAt,
        out QuotaLane lane)
    {
        lane = null!;
        if (value.ValueKind != JsonValueKind.Object) return false;

        double? usedPercent = null;
        if (TryGetDouble(value, out var remainingFraction, "remaining_fraction", "remainingFraction"))
        {
            var clampedRemaining = Math.Clamp(remainingFraction * 100d, 0d, 100d);
            usedPercent = Math.Clamp(100d - clampedRemaining, 0d, 100d);
        }
        else if (TryGetDouble(value, out var remainingPercent, "remaining_percent", "remainingPercent"))
        {
            usedPercent = Math.Clamp(100d - Math.Clamp(remainingPercent, 0d, 100d), 0d, 100d);
        }
        else if (TryGetDouble(value, out var usedFraction, "used_fraction", "usedFraction"))
        {
            usedPercent = Math.Clamp(usedFraction * 100d, 0d, 100d);
        }
        else if (TryGetDouble(value, out var rawUsedPercent, "used_percent", "usedPercent"))
        {
            usedPercent = Math.Clamp(rawUsedPercent, 0d, 100d);
        }

        if (!usedPercent.HasValue) return false;

        DateTimeOffset? resetsAt = null;
        if (TryGetResetTime(value, out var parsedReset))
            resetsAt = parsedReset;
        else if (TryGetDouble(value, out var resetSeconds, "reset_in_seconds", "resetInSeconds") && resetSeconds > 0)
            resetsAt = observedAt.AddSeconds(resetSeconds);

        lane = new QuotaLane(
            key,
            FormatLabel(key),
            usedPercent.Value,
            resetsAt);
        return true;
    }

    private static bool TryGetResetTime(JsonElement element, out DateTimeOffset resetsAt)
    {
        resetsAt = default;
        if (!TryGetProperty(element, "reset_time", out var prop) &&
            !TryGetProperty(element, "resetTime", out prop) &&
            !TryGetProperty(element, "resets_at", out prop) &&
            !TryGetProperty(element, "resetsAt", out prop))
            return false;

        if (prop.ValueKind == JsonValueKind.String)
        {
            var text = prop.GetString();
            if (!string.IsNullOrWhiteSpace(text) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out resetsAt) &&
                resetsAt >= MinimumValidResetTime)
                return true;
        }
        else if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var unixSeconds))
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                if (resetsAt >= MinimumValidResetTime)
                    return true;
            }
            catch (ArgumentOutOfRangeException) { }
        }

        resetsAt = default;
        return false;
    }

    internal static string FormatLabel(string key)
    {
        var words = key
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Trim();
        if (string.IsNullOrWhiteSpace(words)) return "QUOTA LEFT";

        var upper = words.ToUpperInvariant();
        return upper.EndsWith(" LEFT", StringComparison.Ordinal) ? upper : $"{upper} LEFT";
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(root, name, out array) && array.ValueKind == JsonValueKind.Array)
                return true;
        }

        array = default;
        return false;
    }

    private static bool TryGetDouble(JsonElement element, out double value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var prop) &&
                prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetDouble(out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static string? GetOptionalString(JsonElement element, string name)
    {
        if (TryGetProperty(element, name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var text = prop.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
