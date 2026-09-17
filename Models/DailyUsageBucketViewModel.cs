using System;
using System.Globalization;

namespace UsageMonitor.Models;

public sealed class DailyUsageBucketViewModel
{
    public DailyUsageBucketViewModel(DailyUsageBucket bucket, long peakTokens)
    {
        DateText = DateOnly.TryParseExact(
            bucket.StartDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date.ToString("ddd, MMM d", CultureInfo.CurrentCulture)
            : bucket.StartDate;
        TokensText = $"{bucket.Tokens:N0}";
        ProgressValue = peakTokens > 0
            ? Math.Clamp(bucket.Tokens * 100.0 / peakTokens, 0, 100)
            : 0;
    }

    public string DateText { get; }

    public string TokensText { get; }

    public double ProgressValue { get; }
}
