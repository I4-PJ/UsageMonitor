using System.Collections.Generic;
using System.Linq;

namespace UsageMonitor.Models;

public sealed class QuotaLimitViewModel
{
    public QuotaLimitViewModel(QuotaLimit limit)
    {
        LimitId = limit.LimitId;
        DisplayName = limit.DisplayName;
        PlanType = limit.PlanType;
        IsLimitReached = limit.IsLimitReached;

        var windows = new List<QuotaWindowViewModel>();
        if (limit.Primary is not null)
        {
            windows.Add(new QuotaWindowViewModel("5h limit", limit.Primary));
        }

        if (limit.Secondary is not null)
        {
            windows.Add(new QuotaWindowViewModel("7d limit", limit.Secondary));
        }

        Windows = windows;
    }

    public string LimitId { get; }

    public string DisplayName { get; }

    public string? PlanType { get; }

    public bool IsLimitReached { get; }

    public IReadOnlyList<QuotaWindowViewModel> Windows { get; }

    public int LowestLeftPercent => Windows.Count == 0 ? 0 : Windows.Min(window => window.LeftPercent);

    public string StatusText => IsLimitReached ? "Limit reached" : $"{LowestLeftPercent}% minimum remaining";
}
