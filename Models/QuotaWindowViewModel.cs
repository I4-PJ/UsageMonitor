using System;

namespace UsageMonitor.Models;

public sealed class QuotaWindowViewModel
{
    public QuotaWindowViewModel(string title, QuotaWindow window)
    {
        Title = title;
        UsedPercent = window.UsedPercent;
        LeftPercent = window.LeftPercent;
        WindowDurationMinutes = window.WindowDurationMinutes;
        ResetsAt = window.ResetsAt?.ToLocalTime();
    }

    public string Title { get; }

    public int UsedPercent { get; }

    public int LeftPercent { get; }

    public int WindowDurationMinutes { get; }

    public DateTimeOffset? ResetsAt { get; }

    public double ProgressValue => LeftPercent;

    public string LeftText => $"{LeftPercent}% left";

    public string ResetText => ResetsAt is null
        ? "reset unknown"
        : $"resets {ResetsAt:ddd HH:mm}";

    public string WindowText => WindowDurationMinutes >= 1440
        ? $"{WindowDurationMinutes / 1440}d window"
        : $"{WindowDurationMinutes / 60}h window";
}
