using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UsageMonitor.Models;

namespace UsageMonitor.Controls;

public sealed class QuotaHistoryChart : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0F141C"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#222C38")), 1);
    private static readonly IPen ResetPen = new Pen(new SolidColorBrush(Color.Parse("#E9BD70")), 1, DashStyle.Dot);
    private static readonly IPen ObservedPen = new Pen(new SolidColorBrush(Color.Parse("#78E0BE")), 2.5);
    private static readonly IPen ForecastPen = new Pen(new SolidColorBrush(Color.Parse("#7FA8FF")), 2, DashStyle.Dash);
    private static readonly IBrush ObservedBrush = new SolidColorBrush(Color.Parse("#78E0BE"));
    private static readonly IBrush ForecastBrush = new SolidColorBrush(Color.Parse("#7FA8FF"));

    public static readonly StyledProperty<IReadOnlyList<QuotaChartPoint>?> ObservedPointsProperty =
        AvaloniaProperty.Register<QuotaHistoryChart, IReadOnlyList<QuotaChartPoint>?>(nameof(ObservedPoints));

    public static readonly StyledProperty<IReadOnlyList<QuotaChartPoint>?> ForecastPointsProperty =
        AvaloniaProperty.Register<QuotaHistoryChart, IReadOnlyList<QuotaChartPoint>?>(nameof(ForecastPoints));

    public static readonly StyledProperty<IReadOnlyList<DateTimeOffset>?> ResetTimesUtcProperty =
        AvaloniaProperty.Register<QuotaHistoryChart, IReadOnlyList<DateTimeOffset>?>(nameof(ResetTimesUtc));

    public static readonly StyledProperty<DateTimeOffset> RangeStartUtcProperty =
        AvaloniaProperty.Register<QuotaHistoryChart, DateTimeOffset>(nameof(RangeStartUtc));

    public static readonly StyledProperty<DateTimeOffset> RangeEndUtcProperty =
        AvaloniaProperty.Register<QuotaHistoryChart, DateTimeOffset>(nameof(RangeEndUtc));

    static QuotaHistoryChart()
    {
        AffectsRender<QuotaHistoryChart>(
            ObservedPointsProperty,
            ForecastPointsProperty,
            ResetTimesUtcProperty,
            RangeStartUtcProperty,
            RangeEndUtcProperty);
    }

    public IReadOnlyList<QuotaChartPoint>? ObservedPoints
    {
        get => GetValue(ObservedPointsProperty);
        set => SetValue(ObservedPointsProperty, value);
    }

    public IReadOnlyList<QuotaChartPoint>? ForecastPoints
    {
        get => GetValue(ForecastPointsProperty);
        set => SetValue(ForecastPointsProperty, value);
    }

    public IReadOnlyList<DateTimeOffset>? ResetTimesUtc
    {
        get => GetValue(ResetTimesUtcProperty);
        set => SetValue(ResetTimesUtcProperty, value);
    }

    public DateTimeOffset RangeStartUtc
    {
        get => GetValue(RangeStartUtcProperty);
        set => SetValue(RangeStartUtcProperty, value);
    }

    public DateTimeOffset RangeEndUtc
    {
        get => GetValue(RangeEndUtcProperty);
        set => SetValue(RangeEndUtcProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var plot = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (plot.Width <= 1 || plot.Height <= 1)
        {
            return;
        }

        context.DrawRectangle(BackgroundBrush, null, plot);
        for (var index = 0; index <= 4; index++)
        {
            var y = plot.Top + plot.Height * index / 4.0;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        var rangeStart = RangeStartUtc.ToUniversalTime();
        var rangeEnd = RangeEndUtc.ToUniversalTime();
        if (rangeEnd <= rangeStart)
        {
            return;
        }

        using (context.PushClip(plot))
        {
            DrawResetMarkers(context, plot, rangeStart, rangeEnd);
            DrawSeries(context, plot, rangeStart, rangeEnd, ObservedPoints, ObservedPen, ObservedBrush, breakAtGaps: true);
            DrawSeries(context, plot, rangeStart, rangeEnd, ForecastPoints, ForecastPen, ForecastBrush, breakAtGaps: false);
        }
    }

    private void DrawResetMarkers(
        DrawingContext context,
        Rect plot,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        if (ResetTimesUtc is null)
        {
            return;
        }

        foreach (var resetAt in ResetTimesUtc)
        {
            if (resetAt < rangeStart || resetAt > rangeEnd)
            {
                continue;
            }

            var x = MapX(resetAt, plot, rangeStart, rangeEnd);
            context.DrawLine(ResetPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private static void DrawSeries(
        DrawingContext context,
        Rect plot,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        IReadOnlyList<QuotaChartPoint>? points,
        IPen pen,
        IBrush pointBrush,
        bool breakAtGaps)
    {
        if (points is null || points.Count == 0)
        {
            return;
        }

        QuotaChartPoint? previous = null;
        foreach (var point in points)
        {
            if (point.TimestampUtc < rangeStart || point.TimestampUtc > rangeEnd)
            {
                continue;
            }

            var currentLocation = MapPoint(point, plot, rangeStart, rangeEnd);
            if (previous is not null)
            {
                var sameCycle = previous.CycleResetAtUtc == point.CycleResetAtUtc;
                var closeEnough = !breakAtGaps || AreAdjacentUtcHours(previous.TimestampUtc, point.TimestampUtc);
                if (sameCycle && closeEnough)
                {
                    context.DrawLine(
                        pen,
                        MapPoint(previous, plot, rangeStart, rangeEnd),
                        currentLocation);
                }
            }

            previous = point;
        }

        var last = points[^1];
        if (last.TimestampUtc >= rangeStart && last.TimestampUtc <= rangeEnd)
        {
            context.DrawEllipse(pointBrush, null, MapPoint(last, plot, rangeStart, rangeEnd), 3, 3);
        }
    }

    private static Point MapPoint(
        QuotaChartPoint point,
        Rect plot,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var x = MapX(point.TimestampUtc, plot, rangeStart, rangeEnd);
        var remaining = Math.Clamp(point.RemainingPercent, 0, 100);
        var y = plot.Bottom - remaining / 100.0 * plot.Height;
        return new Point(x, y);
    }

    private static double MapX(
        DateTimeOffset timestamp,
        Rect plot,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var fraction = (timestamp.ToUniversalTime() - rangeStart).TotalSeconds /
            (rangeEnd - rangeStart).TotalSeconds;
        return plot.Left + Math.Clamp(fraction, 0, 1) * plot.Width;
    }

    private static bool AreAdjacentUtcHours(DateTimeOffset left, DateTimeOffset right)
    {
        return FloorToUtcHour(right) - FloorToUtcHour(left) <= TimeSpan.FromHours(1);
    }

    private static DateTimeOffset FloorToUtcHour(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
