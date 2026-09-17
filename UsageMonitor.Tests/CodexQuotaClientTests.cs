using System.Text.Json;
using UsageMonitor.Services;
using Xunit;

namespace UsageMonitor.Tests;

public sealed class CodexQuotaClientTests
{
    [Fact]
    public void ParseUsage_RetainsSummaryAndAllDailyBuckets()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "summary": {
                "lifetimeTokens": 123456,
                "peakDailyTokens": 45000,
                "currentStreakDays": 4,
                "longestStreakDays": 12,
                "longestRunningTurnSec": 3661
              },
              "dailyUsageBuckets": [
                { "startDate": "2026-09-17", "tokens": 2500 },
                { "startDate": "2026-09-16", "tokens": 1000 }
              ]
            }
            """);

        var usage = CodexQuotaClient.ParseUsage(document.RootElement);

        Assert.Equal(123456, usage.LifetimeTokens);
        Assert.Equal(45000, usage.PeakDailyTokens);
        Assert.Equal(2500, usage.TodayTokens);
        Assert.Equal(4, usage.CurrentStreakDays);
        Assert.Equal(12, usage.LongestStreakDays);
        Assert.Equal(3661, usage.LongestRunningTurnSeconds);
        Assert.Collection(
            usage.DailyUsageBuckets,
            bucket =>
            {
                Assert.Equal("2026-09-16", bucket.StartDate);
                Assert.Equal(1000, bucket.Tokens);
            },
            bucket =>
            {
                Assert.Equal("2026-09-17", bucket.StartDate);
                Assert.Equal(2500, bucket.Tokens);
            });
    }

    [Fact]
    public void ParseUsage_ToleratesMissingAndMalformedOptionalData()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "summary": null,
              "dailyUsageBuckets": [
                null,
                { "startDate": "", "tokens": 100 },
                { "startDate": "2026-09-17" },
                { "startDate": "2026-09-17", "tokens": -10 }
              ]
            }
            """);

        var usage = CodexQuotaClient.ParseUsage(document.RootElement);

        Assert.Null(usage.LifetimeTokens);
        Assert.Null(usage.PeakDailyTokens);
        Assert.Equal(0, usage.TodayTokens);
        var bucket = Assert.Single(usage.DailyUsageBuckets);
        Assert.Equal("2026-09-17", bucket.StartDate);
        Assert.Equal(0, bucket.Tokens);
    }
}
