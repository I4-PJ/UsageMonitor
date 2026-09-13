using UsageMonitor.Models;
using UsageMonitor.Services;
using Xunit;

namespace UsageMonitor.Tests;

public sealed class QuotaHistoryAnalyzerTests
{
    private readonly QuotaHistoryAnalyzer _analyzer = new();

    [Fact]
    public void CalculateForecast_PredictsDepletionBeforeReset()
    {
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(4);
        var samples = new[]
        {
            Sample(now.AddMinutes(-30), 20, reset, 300),
            Sample(now, 40, reset, 300)
        };

        var forecast = _analyzer.CalculateForecast(samples, now);

        Assert.True(forecast.HasEstimate);
        Assert.NotNull(forecast.ExpectedDepletionAtUtc);
        Assert.True(forecast.ExpectedDepletionAtUtc < reset);
        Assert.Equal(0, forecast.ProjectedRemainingAtReset);
        Assert.Equal("Low", forecast.Confidence);
    }

    [Fact]
    public void CalculateForecast_ReportsRemainingWhenResetComesFirst()
    {
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddDays(2);
        var durationMinutes = 10_080;
        var samples = new[]
        {
            Sample(now.AddHours(-12), 18, reset, durationMinutes),
            Sample(now, 20, reset, durationMinutes)
        };

        var forecast = _analyzer.CalculateForecast(samples, now);

        Assert.True(forecast.HasEstimate);
        Assert.Null(forecast.ExpectedDepletionAtUtc);
        Assert.NotNull(forecast.ProjectedRemainingAtReset);
        Assert.InRange(forecast.ProjectedRemainingAtReset.Value, 0.1, 99.9);
    }

    [Fact]
    public void CalculateForecast_DoesNotUseSamplesAcrossResetBoundary()
    {
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var currentReset = now.AddHours(4);
        var previousReset = now.AddHours(-1);
        var samples = new[]
        {
            Sample(now.AddHours(-2), 95, previousReset, 300),
            Sample(now.AddMinutes(-30), 5, currentReset, 300),
            Sample(now, 10, currentReset, 300)
        };

        var forecast = _analyzer.CalculateForecast(samples, now);

        Assert.True(forecast.HasEstimate);
        Assert.NotNull(forecast.BurnRatePercentPerHour);
        Assert.InRange(forecast.BurnRatePercentPerHour.Value, 1, 30);
    }

    [Fact]
    public void CalculateForecast_ReturnsLearningWithoutResetTime()
    {
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var samples = new[] { Sample(now, 20, null, 300) };

        var forecast = _analyzer.CalculateForecast(samples, now);

        Assert.False(forecast.HasEstimate);
        Assert.Contains("Learning", forecast.Summary);
    }

    [Fact]
    public void BuildCharts_UsesLastSampleInEachUtcHour()
    {
        var now = new DateTimeOffset(2026, 9, 13, 12, 45, 0, TimeSpan.Zero);
        var reset = now.AddDays(2);
        var samples = new[]
        {
            Sample(new DateTimeOffset(2026, 9, 13, 10, 5, 0, TimeSpan.Zero), 10, reset, 10_080),
            Sample(new DateTimeOffset(2026, 9, 13, 10, 55, 0, TimeSpan.Zero), 12, reset, 10_080),
            Sample(new DateTimeOffset(2026, 9, 13, 12, 40, 0, TimeSpan.Zero), 15, reset, 10_080)
        };

        var chart = Assert.Single(_analyzer.BuildCharts(samples, now));

        Assert.Equal(2, chart.ObservedPoints.Count);
        Assert.Equal(88, chart.ObservedPoints[0].RemainingPercent);
        Assert.Equal(85, chart.ObservedPoints[1].RemainingPercent);
        Assert.True(chart.ObservedPoints[1].TimestampUtc - chart.ObservedPoints[0].TimestampUtc > TimeSpan.FromMinutes(90));
    }

    private static QuotaHistorySample Sample(
        DateTimeOffset capturedAt,
        int usedPercent,
        DateTimeOffset? resetsAt,
        int durationMinutes)
    {
        return new QuotaHistorySample(
            capturedAt,
            "codex",
            "Codex",
            "primary",
            durationMinutes,
            resetsAt,
            usedPercent);
    }
}
