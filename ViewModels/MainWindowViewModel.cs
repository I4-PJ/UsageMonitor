using System;
using System.Collections.Generic;
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
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _settingsStore;

    public event Action<QuotaSnapshot>? QuotaRefreshed;

    public event Action? QuotaRefreshFailed;

    public event Action<int>? RefreshIntervalChanged;

    public QuotaSnapshot? LastSnapshot { get; private set; }

    public IReadOnlyList<RefreshIntervalOption> RefreshIntervalOptions { get; } =
    [
        new(1, "1 minute"),
        new(5, "5 minutes"),
        new(15, "15 minutes"),
        new(30, "30 minutes")
    ];

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

    [ObservableProperty]
    private RefreshIntervalOption? _selectedRefreshIntervalOption;

    public ObservableCollection<QuotaLimitViewModel> Limits { get; } = [];

    public MainWindowViewModel()
        : this(new AppSettingsStore())
    {
    }

    public MainWindowViewModel(AppSettingsStore settingsStore)
        : this(settingsStore, settingsStore.Load())
    {
    }

    public MainWindowViewModel(AppSettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        SelectedRefreshIntervalOption = RefreshIntervalOptions.FirstOrDefault(option => option.Minutes == settings.RefreshIntervalMinutes)
            ?? RefreshIntervalOptions.First(option => option.Minutes == 5);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            StatusText = "Refreshing Codex quota...";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var snapshot = await _quotaClient.ReadQuotaAsync(timeout.Token);
            LastSnapshot = snapshot;

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
            QuotaRefreshed?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            HasQuota = false;
            StatusText = "Refresh failed";
            SummaryText = ex.Message;
            LastUpdatedText = $"Failed {DateTimeOffset.Now:HH:mm:ss}";
            QuotaRefreshFailed?.Invoke();
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

    partial void OnSelectedRefreshIntervalOptionChanged(RefreshIntervalOption? value)
    {
        if (value is null || _settings.RefreshIntervalMinutes == value.Minutes)
        {
            return;
        }

        _settings.RefreshIntervalMinutes = value.Minutes;
        _settingsStore.Save(_settings);
        RefreshIntervalChanged?.Invoke(value.Minutes);
    }
}
