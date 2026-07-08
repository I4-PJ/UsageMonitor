using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsageMonitor.Models;
using UsageMonitor.Services;

namespace UsageMonitor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly CodexQuotaClient _quotaClient = new();

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _lastUpdatedText = "Not refreshed yet";

    [ObservableProperty]
    private string _summaryText = "Refresh to read Codex quota.";

    [ObservableProperty]
    private string _todayTokensText = "Today tokens: unknown";

    [ObservableProperty]
    private string _resetCreditsText = "Reset credits: unknown";

    [ObservableProperty]
    private bool _hasQuota;

    public ObservableCollection<QuotaLimitViewModel> Limits { get; } = [];

    public MainWindowViewModel()
    {
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            StatusText = "Refreshing Codex quota...";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var snapshot = await _quotaClient.ReadQuotaAsync(timeout.Token);

            Limits.Clear();
            foreach (var limit in snapshot.Limits.Select(limit => new QuotaLimitViewModel(limit)))
            {
                Limits.Add(limit);
            }

            HasQuota = Limits.Count > 0;
            LastUpdatedText = $"Updated {snapshot.RefreshedAt:HH:mm:ss}";
            SummaryText = BuildSummaryText();
            ResetCreditsText = snapshot.AvailableResetCredits is null
                ? "Reset credits: unknown"
                : $"Reset credits: {snapshot.AvailableResetCredits}";
            TodayTokensText = snapshot.Usage?.TodayTokens is null
                ? "Today tokens: unknown"
                : $"Today tokens: {snapshot.Usage.TodayTokens.Value:N0}";
            StatusText = "Quota loaded";
        }
        catch (Exception ex)
        {
            HasQuota = false;
            StatusText = "Refresh failed";
            SummaryText = ex.Message;
            LastUpdatedText = $"Failed {DateTimeOffset.Now:HH:mm:ss}";
        }
    }

    private string BuildSummaryText()
    {
        if (Limits.Count == 0)
        {
            return "No quota windows returned.";
        }

        var main = Limits.FirstOrDefault(limit => limit.LimitId == "codex") ?? Limits[0];
        return $"{main.DisplayName}: {main.StatusText}";
    }
}
