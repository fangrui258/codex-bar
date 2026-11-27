using System.Text.Json;

namespace CodexBar;

internal sealed class CodexUsageProvider : IUsageProvider
{
    public const string ProviderId = "codex";
    private readonly CodexAppServerClient client = new();

    public string Id => ProviderId;
    public string TabLabel => "CODEX";
    public string DisplayName => "Codex";

    public bool CheckAccountChanged() => client.RestartIfAuthenticationChanged();

    public async Task<ProviderSnapshot> ReadUsageAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usage = await client.ReadUsageAsync();
        return ToProviderSnapshot(usage);
    }

    public static ProviderSnapshot ToProviderSnapshot(UsageSnapshot usage)
    {
        var lanes = new List<QuotaLane>(2)
        {
            usage.FiveHour is { } fiveHour
                ? new QuotaLane("five-hour", "5-HOUR LEFT", fiveHour.UsedPercent, fiveHour.ResetsAt)
                : new QuotaLane("five-hour", "5-HOUR LEFT", null, null, UnavailableText: "Not reported by Codex"),
            new QuotaLane(
                "weekly",
                "WEEKLY LEFT",
                usage.Weekly.UsedPercent,
                usage.Weekly.ResetsAt,
                usage.ResetExpirations)
        };

        return new ProviderSnapshot(
            ProviderId,
            "CODEX",
            "Codex",
            ProviderStatus.Online,
            lanes,
            usage.CapturedAt,
            AlertLaneKeys: new[] { "weekly" });
    }

    public void Dispose() => client.Dispose();
}
