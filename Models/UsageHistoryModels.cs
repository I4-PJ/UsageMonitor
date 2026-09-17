using System;

namespace UsageMonitor.Models;

public sealed record UsageHistorySample(
    DateTimeOffset CapturedAtUtc,
    string? BucketStartDate,
    long? TodayTokens,
    long? LifetimeTokens,
    long? PeakDailyTokens,
    int? CurrentStreakDays,
    int? LongestStreakDays,
    long? LongestRunningTurnSeconds);
