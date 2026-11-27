namespace CodexBar;

internal enum ProviderStatus
{
    Online,
    Standby,
    Offline
}

internal sealed record QuotaLane(
    string Key,
    string Label,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    IReadOnlyList<DateTimeOffset>? ExpirationTimes = null,
    string? UnavailableText = null)
{
    public double? RemainingPercent => UsedPercent.HasValue
        ? Math.Clamp(100 - UsedPercent.Value, 0, 100)
        : null;
}

internal sealed record ProviderSnapshot(
    string ProviderId,
    string TabLabel,
    string DisplayName,
    ProviderStatus Status,
    IReadOnlyList<QuotaLane> Lanes,
    DateTimeOffset CapturedAt,
    string? StatusMessage = null,
    string? AccountKey = null,
    IReadOnlyList<string>? AlertLaneKeys = null);

internal interface IUsageProvider : IDisposable
{
    string Id { get; }
    string TabLabel { get; }
    string DisplayName { get; }
    bool CheckAccountChanged();
    Task<ProviderSnapshot> ReadUsageAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}
