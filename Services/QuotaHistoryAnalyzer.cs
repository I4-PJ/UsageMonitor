using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UsageMonitor.Models;

namespace UsageMonitor.Services;

public sealed class QuotaHistoryAnalyzer
{
    private static readonly TimeSpan ChartHistory = TimeSpan.FromDays(14);

    public IReadOnlyList<QuotaHistoryChartViewModel> BuildCharts(
        IEnumerable<QuotaHistorySample> history,
        DateTimeOffset now)
    {
        var nowUtc = now.ToUniversalTime();
        var rangeStartUtc = nowUtc - ChartHistory;
        var samples = history
            .Where(sample => sample.CapturedAtUtc >= rangeStartUtc - TimeSpan.FromHours(1))
            .Where(sample => sample.CapturedAtUtc <= nowUtc + TimeSpan.FromMinutes(5))
            .OrderBy(sample => sample.CapturedAtUtc)
            .ToArray();

        return samples
            .GroupBy(sample => new
            {
                sample.LimitId,
                sample.WindowRole,
                sample.WindowDurationMinutes
            })
            .Select(group => BuildChart(group.ToArray(), rangeStartUtc, nowUtc))
            .OrderBy(chart => chart.Title.Contains("Codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(chart => chart.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public QuotaForecast CalculateForecast(
        IReadOnlyList<QuotaHistorySample> samples,
        DateTimeOffset now)
    {
        if (samples.Count == 0)
        {
            return Learning("Waiting for the first quota sample.");
        }

        var nowUtc = now.ToUniversalTime();
        var ordered = samples
            .Where(sample => sample.CapturedAtUtc <= nowUtc + TimeSpan.FromMinutes(5))
            .OrderBy(sample => sample.CapturedAtUtc)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Learning("Waiting for a current quota sample.");
        }

        var latest = ordered[^1];
        if (latest.RemainingPercent <= 0)
        {
            return new QuotaForecast(
                true,
                true,
                latest.CapturedAtUtc,
                0,
                null,
                "Observed",
                "Quota depleted",
                "The latest sample reports 0% remaining.");
        }

        if (latest.ResetsAtUtc is null || latest.ResetsAtUtc <= latest.CapturedAtUtc)
        {
            return Learning("A future reset time is required before depletion can be estimated.");
        }

        var resetAtUtc = latest.ResetsAtUtc.Value;
        var duration = TimeSpan.FromMinutes(latest.WindowDurationMinutes);
        var cycleStartUtc = resetAtUtc - duration;
        var minimumSpanHours = latest.WindowDurationMinutes <= 360 ? 0.5 : 3.0;
        var maximumRecentHours = latest.WindowDurationMinutes <= 360
            ? Math.Max(duration.TotalHours, minimumSpanHours)
            : Math.Min(duration.TotalHours, 24.0);

        var currentCycle = ordered
            .Where(sample => sample.ResetsAtUtc == latest.ResetsAtUtc)
            .Where(sample => sample.CapturedAtUtc >= cycleStartUtc - TimeSpan.FromMinutes(5))
            .ToArray();
        var rateCandidates = new List<double>();

        foreach (var anchor in currentCycle)
        {
            var elapsedHours = (latest.CapturedAtUtc - anchor.CapturedAtUtc).TotalHours;
            var usedDelta = latest.UsedPercent - anchor.UsedPercent;
            if (elapsedHours >= minimumSpanHours &&
                elapsedHours <= maximumRecentHours &&
                usedDelta > 0)
            {
                rateCandidates.Add(usedDelta / elapsedHours);
            }
        }

        var elapsedCycleHours = (latest.CapturedAtUtc - cycleStartUtc).TotalHours;
        if (elapsedCycleHours >= minimumSpanHours && latest.UsedPercent >= 2)
        {
            rateCandidates.Add(latest.UsedPercent / elapsedCycleHours);
        }

        var historicalCycleRates = ordered
            .Where(sample => sample.ResetsAtUtc is not null && sample.ResetsAtUtc != latest.ResetsAtUtc)
            .GroupBy(sample => sample.ResetsAtUtc)
            .Select(group => CalculateObservedRate(group.OrderBy(sample => sample.CapturedAtUtc).ToArray(), minimumSpanHours))
            .Where(rate => rate is > 0)
            .Select(rate => rate!.Value)
            .TakeLast(12)
            .ToArray();
        rateCandidates.AddRange(historicalCycleRates);

        var burnRate = Median(rateCandidates.Where(rate => rate is > 0 and <= 200).ToArray());
        if (burnRate is null)
        {
            return Learning("No measurable quota movement has been recorded in this reset cycle yet.");
        }

        var firstCurrent = currentCycle.FirstOrDefault() ?? latest;
        var observedSpanHours = Math.Max(0, (latest.CapturedAtUtc - firstCurrent.CapturedAtUtc).TotalHours);
        var observedMovement = Math.Max(0, latest.UsedPercent - firstCurrent.UsedPercent);
        var confidence = DetermineConfidence(
            latest.WindowDurationMinutes,
            observedSpanHours,
            observedMovement,
            historicalCycleRates.Length);

        var hoursToReset = Math.Max(0, (resetAtUtc - latest.CapturedAtUtc).TotalHours);
        var hoursToDepletion = latest.RemainingPercent / burnRate.Value;
        var expectedDepletionAtUtc = latest.CapturedAtUtc.AddHours(hoursToDepletion);
        var resetText = resetAtUtc.ToLocalTime().ToString("ddd HH:mm", CultureInfo.CurrentCulture);

        if (expectedDepletionAtUtc < resetAtUtc)
        {
            return new QuotaForecast(
                true,
                false,
                expectedDepletionAtUtc,
                0,
                burnRate,
                confidence,
                $"Likely depleted {FormatForecastTime(expectedDepletionAtUtc)}",
                $"{burnRate:0.0}% per hour · {confidence.ToLowerInvariant()} confidence · resets {resetText}.");
        }

        var projectedRemaining = Math.Clamp(
            latest.RemainingPercent - burnRate.Value * hoursToReset,
            0,
            100);
        return new QuotaForecast(
            true,
            false,
            null,
            projectedRemaining,
            burnRate,
            confidence,
            "Not expected to deplete before reset",
            $"About {projectedRemaining:0}% projected at the {resetText} reset · {confidence.ToLowerInvariant()} confidence.");
    }

    private QuotaHistoryChartViewModel BuildChart(
        IReadOnlyList<QuotaHistorySample> samples,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset nowUtc)
    {
        var latest = samples[^1];
        var observedPoints = samples
            .Where(sample => sample.CapturedAtUtc >= rangeStartUtc)
            .GroupBy(sample => FloorToUtcHour(sample.CapturedAtUtc))
            .Select(group => group.OrderBy(sample => sample.CapturedAtUtc).Last())
            .OrderBy(sample => sample.CapturedAtUtc)
            .Select(sample => new QuotaChartPoint(
                sample.CapturedAtUtc,
                sample.RemainingPercent,
                sample.ResetsAtUtc))
            .ToArray();

        var forecast = CalculateForecast(samples, nowUtc);
        var forecastPoints = BuildForecastPoints(latest, forecast);
        var rangeEndUtc = forecastPoints.Count > 0
            ? Max(nowUtc, forecastPoints[^1].TimestampUtc)
            : nowUtc;
        var resetTimes = samples
            .Select(sample => sample.ResetsAtUtc)
            .Where(reset => reset >= rangeStartUtc && reset <= rangeEndUtc)
            .Select(reset => reset!.Value)
            .Distinct()
            .OrderBy(reset => reset)
            .ToArray();
        var roleLabel = latest.WindowRole.Equals("secondary", StringComparison.OrdinalIgnoreCase)
            ? " · secondary"
            : string.Empty;
        var title = $"{latest.DisplayName} · {QuotaWindowLabels.FormatDuration(latest.WindowDurationMinutes)}{roleLabel}";

        return new QuotaHistoryChartViewModel(
            latest.LimitId,
            latest.WindowRole,
            latest.WindowDurationMinutes,
            title,
            $"{latest.RemainingPercent}% remaining",
            forecast.Summary,
            forecast.Detail,
            rangeStartUtc.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture),
            rangeEndUtc <= nowUtc.AddMinutes(1)
                ? "Now"
                : rangeEndUtc.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture),
            rangeStartUtc,
            rangeEndUtc,
            observedPoints,
            forecastPoints,
            resetTimes);
    }

    private static IReadOnlyList<QuotaChartPoint> BuildForecastPoints(
        QuotaHistorySample latest,
        QuotaForecast forecast)
    {
        if (!forecast.HasEstimate || forecast.IsDepleted || latest.ResetsAtUtc is null)
        {
            return Array.Empty<QuotaChartPoint>();
        }

        var endTime = forecast.ExpectedDepletionAtUtc ?? latest.ResetsAtUtc.Value;
        var endRemaining = forecast.ExpectedDepletionAtUtc is not null
            ? 0
            : forecast.ProjectedRemainingAtReset ?? latest.RemainingPercent;
        return
        [
            new QuotaChartPoint(latest.CapturedAtUtc, latest.RemainingPercent, latest.ResetsAtUtc),
            new QuotaChartPoint(endTime, endRemaining, latest.ResetsAtUtc)
        ];
    }

    private static double? CalculateObservedRate(
        IReadOnlyList<QuotaHistorySample> cycle,
        double minimumSpanHours)
    {
        if (cycle.Count < 2)
        {
            return null;
        }

        var first = cycle[0];
        var last = cycle[^1];
        var elapsedHours = (last.CapturedAtUtc - first.CapturedAtUtc).TotalHours;
        var usedDelta = last.UsedPercent - first.UsedPercent;
        return elapsedHours >= minimumSpanHours && usedDelta > 0
            ? usedDelta / elapsedHours
            : null;
    }

    private static string DetermineConfidence(
        int durationMinutes,
        double observedSpanHours,
        int observedMovement,
        int historicalCycleCount)
    {
        var durationHours = durationMinutes / 60.0;
        if (observedSpanHours >= Math.Min(12, durationHours * 0.25) &&
            observedMovement >= 8 &&
            historicalCycleCount >= 3)
        {
            return "High";
        }

        if (observedSpanHours >= Math.Min(4, durationHours * 0.15) && observedMovement >= 3)
        {
            return "Medium";
        }

        return "Low";
    }

    private static QuotaForecast Learning(string detail)
    {
        return new QuotaForecast(
            false,
            false,
            null,
            null,
            null,
            "Learning",
            "Learning usage pattern",
            detail);
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static DateTimeOffset FloorToUtcHour(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
    {
        return left >= right ? left : right;
    }

    private static string FormatForecastTime(DateTimeOffset timestampUtc)
    {
        var local = timestampUtc.ToLocalTime();
        var now = DateTimeOffset.Now;
        return local.Date == now.Date
            ? local.ToString("'today at' HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }
}
