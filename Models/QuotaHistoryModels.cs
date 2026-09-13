using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UsageMonitor.Models;

public sealed record QuotaHistorySample(
    DateTimeOffset CapturedAtUtc,
    string LimitId,
    string DisplayName,
    string WindowRole,
    int WindowDurationMinutes,
    DateTimeOffset? ResetsAtUtc,
    int UsedPercent)
{
    [JsonIgnore]
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record QuotaChartPoint(
    DateTimeOffset TimestampUtc,
    double RemainingPercent,
    DateTimeOffset? CycleResetAtUtc);

public sealed record QuotaForecast(
    bool HasEstimate,
    bool IsDepleted,
    DateTimeOffset? ExpectedDepletionAtUtc,
    double? ProjectedRemainingAtReset,
    double? BurnRatePercentPerHour,
    string Confidence,
    string Summary,
    string Detail);

public sealed class QuotaHistoryChartViewModel
{
    public QuotaHistoryChartViewModel(
        string limitId,
        string windowRole,
        int windowDurationMinutes,
        string title,
        string currentStatus,
        string forecastText,
        string forecastDetail,
        string rangeStartText,
        string rangeEndText,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        IReadOnlyList<QuotaChartPoint> observedPoints,
        IReadOnlyList<QuotaChartPoint> forecastPoints,
        IReadOnlyList<DateTimeOffset> resetTimesUtc)
    {
        LimitId = limitId;
        WindowRole = windowRole;
        WindowDurationMinutes = windowDurationMinutes;
        Title = title;
        CurrentStatus = currentStatus;
        ForecastText = forecastText;
        ForecastDetail = forecastDetail;
        RangeStartText = rangeStartText;
        RangeEndText = rangeEndText;
        RangeStartUtc = rangeStartUtc;
        RangeEndUtc = rangeEndUtc;
        ObservedPoints = observedPoints;
        ForecastPoints = forecastPoints;
        ResetTimesUtc = resetTimesUtc;
    }

    public string LimitId { get; }

    public string WindowRole { get; }

    public int WindowDurationMinutes { get; }

    public string Title { get; }

    public string CurrentStatus { get; }

    public string ForecastText { get; }

    public string ForecastDetail { get; }

    public string RangeStartText { get; }

    public string RangeEndText { get; }

    public DateTimeOffset RangeStartUtc { get; }

    public DateTimeOffset RangeEndUtc { get; }

    public IReadOnlyList<QuotaChartPoint> ObservedPoints { get; }

    public IReadOnlyList<QuotaChartPoint> ForecastPoints { get; }

    public IReadOnlyList<DateTimeOffset> ResetTimesUtc { get; }
}
