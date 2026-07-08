using System;
using System.Collections.Generic;

namespace UsageMonitor.Models;

public sealed record QuotaSnapshot(
    DateTimeOffset RefreshedAt,
    IReadOnlyList<QuotaLimit> Limits,
    int? AvailableResetCredits,
    UsageSummary? Usage);

public sealed record QuotaLimit(
    string LimitId,
    string DisplayName,
    string? PlanType,
    QuotaWindow? Primary,
    QuotaWindow? Secondary,
    bool IsLimitReached,
    bool? HasCredits,
    bool? HasUnlimitedCredits);

public sealed record QuotaWindow(
    int UsedPercent,
    int LeftPercent,
    int WindowDurationMinutes,
    DateTimeOffset? ResetsAt);

public sealed record UsageSummary(
    long? LifetimeTokens,
    long? PeakDailyTokens,
    long? TodayTokens,
    int? CurrentStreakDays,
    int? LongestStreakDays);
