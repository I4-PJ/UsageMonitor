using UsageMonitor.Models;
using UsageMonitor.Services;
using Xunit;

namespace UsageMonitor.Tests;

public sealed class QuotaHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"UsageMonitor.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task RecordAndLoad_RoundTripsAllQuotaWindows()
    {
        var path = Path.Combine(_directory, "history.jsonl");
        var store = new QuotaHistoryStore(path);
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = new QuotaSnapshot(
            now,
            [
                new QuotaLimit(
                    "codex",
                    "Codex",
                    "pro",
                    new QuotaWindow(25, 75, 300, now.AddHours(4)),
                    new QuotaWindow(50, 50, 10_080, now.AddDays(5)),
                    false,
                    false,
                    false)
            ],
            1);

        await store.RecordAsync(snapshot);
        var loaded = await store.LoadAsync(now.AddDays(-1));

        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, sample => Assert.Equal(TimeSpan.Zero, sample.CapturedAtUtc.Offset));
        Assert.Contains(loaded, sample => sample.WindowRole == "primary" && sample.UsedPercent == 25);
        Assert.Contains(loaded, sample => sample.WindowRole == "secondary" && sample.UsedPercent == 50);
    }

    [Fact]
    public async Task Load_SkipsMalformedLines()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.jsonl");
        var store = new QuotaHistoryStore(path);
        var now = DateTimeOffset.UtcNow;
        var valid = new QuotaHistorySample(now, "codex", "Codex", "primary", 300, null, 15);
        await File.WriteAllLinesAsync(path,
        [
            "not json",
            System.Text.Json.JsonSerializer.Serialize(valid)
        ]);

        var loaded = await store.LoadAsync(now.AddMinutes(-1));

        var sample = Assert.Single(loaded);
        Assert.Equal(15, sample.UsedPercent);
    }

    [Fact]
    public async Task Record_CompactsExpiredSamples()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.jsonl");
        var now = DateTimeOffset.UtcNow;
        var old = new QuotaHistorySample(now.AddDays(-20), "codex", "Codex", "primary", 300, null, 10);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(old) + Environment.NewLine);
        var store = new QuotaHistoryStore(path, TimeSpan.FromDays(15));
        var snapshot = new QuotaSnapshot(
            now,
            [new QuotaLimit("codex", "Codex", "pro", new QuotaWindow(20, 80, 300, now.AddHours(4)), null, false, false, false)],
            null);

        await store.RecordAsync(snapshot);
        var loaded = await store.LoadAsync(DateTimeOffset.MinValue);

        var sample = Assert.Single(loaded);
        Assert.Equal(20, sample.UsedPercent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
