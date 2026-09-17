using UsageMonitor.Models;
using UsageMonitor.Services;
using Xunit;

namespace UsageMonitor.Tests;

public sealed class UsageHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"UsageMonitor.UsageTests.{Guid.NewGuid():N}");

    [Fact]
    public async Task RecordAndLoad_RoundTripsAccountUsageSnapshot()
    {
        var path = Path.Combine(_directory, "usage-history.jsonl");
        var store = new UsageHistoryStore(path);
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.FromHours(2));
        var usage = new UsageSummary(
            100_000,
            25_000,
            12_000,
            3,
            8,
            3_661,
            [new DailyUsageBucket("2026-09-17", 12_000)]);
        var snapshot = new QuotaSnapshot(now, [], null, usage);

        await store.RecordAsync(snapshot);
        var loaded = await store.LoadAsync(now.AddDays(-1));

        var sample = Assert.Single(loaded);
        Assert.Equal(TimeSpan.Zero, sample.CapturedAtUtc.Offset);
        Assert.Equal("2026-09-17", sample.BucketStartDate);
        Assert.Equal(12_000, sample.TodayTokens);
        Assert.Equal(100_000, sample.LifetimeTokens);
        Assert.Equal(3_661, sample.LongestRunningTurnSeconds);
    }

    [Fact]
    public async Task Record_DoesNothingWhenUsageIsUnavailable()
    {
        var path = Path.Combine(_directory, "usage-history.jsonl");
        var store = new UsageHistoryStore(path);
        var snapshot = new QuotaSnapshot(DateTimeOffset.UtcNow, [], null, null);

        await store.RecordAsync(snapshot);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Load_SkipsMalformedLinesAndNormalizesValues()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "usage-history.jsonl");
        var store = new UsageHistoryStore(path);
        var now = DateTimeOffset.UtcNow;
        var valid = new UsageHistorySample(now, "2026-09-17", -10, 100, 50, -1, 2, -30);
        await File.WriteAllLinesAsync(path,
        [
            "not json",
            System.Text.Json.JsonSerializer.Serialize(valid)
        ]);

        var loaded = await store.LoadAsync(now.AddMinutes(-1));

        var sample = Assert.Single(loaded);
        Assert.Equal(0, sample.TodayTokens);
        Assert.Equal(0, sample.CurrentStreakDays);
        Assert.Equal(0, sample.LongestRunningTurnSeconds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
